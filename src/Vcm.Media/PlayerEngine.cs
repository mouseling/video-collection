using LibVLCSharp.Shared;
using Vcm.Core;

using VlcMedia = LibVLCSharp.Shared.Media;

namespace Vcm.Media;

/// <summary>
/// Drives libvlc and keeps a <see cref="PlaybackTracker"/> in step with it.
///
/// Events are raised on libvlc's own threads; callers on a UI toolkit must marshal them. Nothing
/// here touches Avalonia, so the engine stays usable from the headless CLI.
/// </summary>
public sealed class PlayerEngine : IDisposable
{
    private readonly LibVLC _vlc;
    private readonly Library _library;
    private readonly Database _db;
    private readonly Timer _ticker;

    private PlaybackTracker? _tracker;
    private VideoStats? _current;
    private PlayQueue _queue = PlayQueue.Empty;
    private bool _disposed;

    /// <summary>250 ms keeps watch time accurate to a quarter second and the seek bar smooth.</summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(250);

    public MediaPlayer Player { get; }

    public VideoStats? Current => _current;
    public PlayQueue Queue => _queue;
    public int CurrentVote => _tracker?.Vote ?? 0;
    public bool IsPlaying => Player.IsPlaying;

    /// <summary>Position in milliseconds, and the video's length.</summary>
    public long PositionMs => Player.Time < 0 ? 0 : Player.Time;
    public long DurationMs => _current?.DurationMs ?? (Player.Length < 0 ? 0 : Player.Length);

    public event Action? Tick;
    public event Action<VideoStats?>? TrackChanged;
    public event Action? QueueExhausted;

    public PlayerEngine(LibVLC vlc, Library library)
    {
        _vlc = vlc;
        _library = library;
        _db = library.Db;

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
        CloseSession();

        _current = video;
        if (queue is not null) _queue = queue;

        var media = new VlcMedia(_vlc, video.Path, FromType.FromPath);
        Player.Play(media);
        MediaReaper.Retire(media);

        _tracker = new PlaybackTracker(_db, video.VideoId);
        TrackChanged?.Invoke(video);
    }

    public void PlayAt(int queueIndex)
    {
        var next = _queue.MoveTo(queueIndex);
        if (next is not null) Play(next);
    }

    public bool Next()
    {
        var next = _queue.MoveNext();
        if (next is null) return false;
        Play(next);
        return true;
    }

    public bool Previous()
    {
        var prev = _queue.MovePrevious();
        if (prev is null) return false;
        Play(prev);
        return true;
    }

    public void TogglePause()
    {
        if (_current is null) return;
        Player.Pause();
    }

    public void Stop()
    {
        CloseSession();
        _current = null;
        Player.Stop();
        TrackChanged?.Invoke(null);
    }

    /// <summary>Seeks to a fraction of the video. The tracker notices and closes the region.</summary>
    public void SeekFraction(double fraction)
    {
        if (_current is null) return;
        Player.Position = (float)Math.Clamp(fraction, 0, 0.999);
    }

    // ------------------------------------------------------------------ votes and tags

    /// <summary>One vote per session; returns false once this session has already voted.</summary>
    public bool CastVote(int vote) => _tracker?.CastVote(vote) ?? false;

    public void TagCurrent(string name)
    {
        if (_current is null || string.IsNullOrWhiteSpace(name)) return;
        _library.AddTag(_current.VideoId, name.Trim());
    }

    /// <summary>Replay heat for the current video, for painting the seek bar.</summary>
    public double[] CurrentHeat(int buckets)
    {
        if (_current is null) return new double[buckets];
        var duration = DurationMs;
        return duration <= 0
            ? new double[buckets]
            : Heatmap.ForVideo(_db, _current.VideoId, duration, buckets);
    }

    // ------------------------------------------------------------------ internals

    private void OnTick()
    {
        if (_disposed) return;

        try
        {
            if (_tracker is not null && _current is not null)
                _tracker.Update(PositionMs, Player.IsPlaying);

            Tick?.Invoke();
        }
        catch (ObjectDisposedException) { /* shutting down */ }
    }

    private void OnEndReached(object? sender, EventArgs e)
    {
        // libvlc forbids re-entering the player from its own event thread.
        Task.Run(() =>
        {
            CloseSession();
            if (!Next()) QueueExhausted?.Invoke();
        });
    }

    /// <summary>Finalises the current session so its watch time and regions are persisted.</summary>
    private void CloseSession()
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
        if (_disposed) return;
        _disposed = true;

        _ticker.Dispose();
        CloseSession();

        Player.EndReached -= OnEndReached;
        try { Player.Stop(); } catch { /* already gone */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _ticker.Dispose();
        CloseSession();

        Player.EndReached -= OnEndReached;
        try { Player.Stop(); } catch { /* ignored */ }
        Player.Dispose();
    }
}
