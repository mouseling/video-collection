using LibVLCSharp.Shared;
using Vcm.Core;

using VlcMedia = LibVLCSharp.Shared.Media;

namespace Vcm.Media;

/// <summary>
/// Drives libvlc and keeps a <see cref="PlaybackTracker"/> in step with it.
///
/// Three threads reach into this object: the 250 ms tick on a threadpool thread, the UI thread
/// (transport buttons and votes), and libvlc's own event thread when a track ends. Every one of
/// them touches the same tracker, so all state transitions are serialised on <see cref="_state"/>.
/// Without it, the end of each track races its own final tick and loses watch time or writes a
/// region twice — on every queue advance, which is the app's normal mode.
///
/// Events are raised on whichever thread caused them; callers on a UI toolkit must marshal.
/// Nothing here touches Avalonia, so the engine stays usable from the headless CLI.
/// </summary>
public sealed class PlayerEngine : IDisposable
{
    private readonly LibVLC _vlc;
    private readonly Library _library;
    private readonly Timer _ticker;
    private readonly object _state = new();

    private PlaybackTracker? _tracker;
    private VideoStats? _current;
    private PlayQueue _queue = PlayQueue.Empty;
    private bool _disposed;

    /// <summary>250 ms keeps watch time accurate to a quarter second and the seek bar smooth.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    public MediaPlayer Player { get; }

    public VideoStats? Current { get { lock (_state) return _current; } }
    public PlayQueue Queue { get { lock (_state) return _queue; } }
    public int CurrentVote { get { lock (_state) return _tracker?.Vote ?? 0; } }
    public bool IsPlaying => Player.IsPlaying;

    public long PositionMs => Player.Time < 0 ? 0 : Player.Time;

    /// <summary>
    /// Length of the current video. The stored duration is preferred because it survives a
    /// video that libvlc is slow to report on, but a stored 0 means "not probed yet" and must
    /// fall through to the player — note that `_current?.DurationMs ?? …` does NOT do this, since
    /// a non-null video with duration 0 yields (long?)0, which is not null.
    /// </summary>
    public long DurationMs
    {
        get
        {
            var stored = Current?.DurationMs ?? 0;
            if (stored > 0) return stored;
            return Player.Length < 0 ? 0 : Player.Length;
        }
    }

    public event Action? Tick;
    public event Action<VideoStats?>? TrackChanged;
    public event Action? QueueExhausted;

    public PlayerEngine(LibVLC vlc, Library library)
    {
        _vlc = vlc;
        _library = library;

        Player = new MediaPlayer(_vlc) { EnableKeyInput = false, EnableMouseInput = false };
        Player.EndReached += OnEndReached;

        _ticker = new Timer(_ => OnTick(), null, TickInterval, TickInterval);
    }

    // ------------------------------------------------------------------ playback

    /// <summary>
    /// Starts a video. Playback always begins at the beginning — resuming from the last position
    /// is deliberately not offered.
    /// </summary>
    public void Play(VideoStats video, PlayQueue? queue = null)
    {
        lock (_state)
        {
            if (_disposed) return;

            CloseSessionLocked();

            _current = video;
            if (queue is not null) _queue = queue;

            _tracker = _library.StartSession(video.VideoId);
        }

        // Outside the lock: Play() blocks while libvlc opens the file, and holding the state lock
        // across it would stall the tick thread and the UI for the duration.
        var media = new VlcMedia(_vlc, video.Path, FromType.FromPath);
        Player.Play(media);
        MediaReaper.Retire(media);

        TrackChanged?.Invoke(video);
    }

    public bool Next()
    {
        VideoStats? next;
        lock (_state) next = _queue.MoveNext();

        if (next is null) return false;
        Play(next);
        return true;
    }

    public bool Previous()
    {
        VideoStats? prev;
        lock (_state) prev = _queue.MovePrevious();

        if (prev is null) return false;
        Play(prev);
        return true;
    }

    public void TogglePause()
    {
        if (Current is null) return;
        Player.Pause();
    }

    public void Stop()
    {
        lock (_state)
        {
            CloseSessionLocked();
            _current = null;
        }

        Player.Stop();
        TrackChanged?.Invoke(null);
    }

    /// <summary>Seeks to a fraction of the video. The tracker notices and closes the region.</summary>
    public void SeekFraction(double fraction)
    {
        if (Current is null) return;

        // Never seek to exactly 1.0: libvlc treats that as the end of the stream and fires
        // EndReached, which would skip to the next track instead of seeking within this one.
        Player.Position = (float)Math.Clamp(fraction, 0, 0.999);
    }

    // ------------------------------------------------------------------ votes and tags

    /// <summary>One vote per session; returns false once this session has already voted.</summary>
    public bool CastVote(int vote)
    {
        lock (_state) return _tracker?.CastVote(vote) ?? false;
    }

    public void TagCurrent(string name)
    {
        var video = Current;
        if (video is null || string.IsNullOrWhiteSpace(name)) return;
        _library.AddTag(video.VideoId, name.Trim());
    }

    /// <summary>Replay heat for the current video, for painting the seek bar.</summary>
    public double[] CurrentHeat(int buckets)
    {
        var video = Current;
        var duration = DurationMs;

        return video is null || duration <= 0
            ? new double[buckets]
            : _library.GetHeat(video.VideoId, duration, buckets);
    }

    // ------------------------------------------------------------------ internals

    private void OnTick()
    {
        try
        {
            lock (_state)
            {
                if (_disposed || _tracker is null || _current is null) return;
                _tracker.Update(PositionMs, Player.IsPlaying);
            }

            Tick?.Invoke();
        }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        // libvlc forbids re-entering the player from its own event thread.
        Task.Run(() =>
        {
            try
            {
                lock (_state) CloseSessionLocked();

                if (Next()) return;

                // Nothing left to play. The current video must be cleared, otherwise the vote
                // buttons stay enabled over a finished session whose tracker is gone, and a press
                // is silently swallowed as "already voted".
                lock (_state) _current = null;
                TrackChanged?.Invoke(null);
                QueueExhausted?.Invoke();
            }
            catch (Exception ex)
            {
                // An unobserved exception here would stop the queue advancing for the rest of the
                // session with nothing to show for it.
                Log.Write("queue advance failed", ex);
            }
        });
    }

    /// <summary>
    /// Finalises the current session so its watch time and regions are persisted.
    /// Caller must hold <see cref="_state"/>.
    /// </summary>
    private void CloseSessionLocked()
    {
        if (_tracker is null) return;

        _tracker.Update(PositionMs, Player.IsPlaying);
        _tracker.Finish();
        _tracker = null;
    }

    /// <summary>
    /// Shutdown path for application exit: persists the open session and halts playback, but
    /// deliberately does not dispose the native player or libvlc. Tearing those down while VLC's
    /// own threads are still unwinding segfaults the process, and at exit there is nothing to
    /// reclaim that the OS will not reclaim anyway.
    /// </summary>
    public void ShutdownForExit()
    {
        lock (_state)
        {
            if (_disposed) return;
            _disposed = true;
            CloseSessionLocked();
        }

        _ticker.Dispose();
        Player.EndReached -= OnEndReached;
        try { Player.Stop(); } catch { /* already gone */ }
    }

    public void Dispose()
    {
        ShutdownForExit();
        Player.Dispose();
    }
}
