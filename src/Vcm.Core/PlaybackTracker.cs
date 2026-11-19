namespace Vcm.Core;

/// <summary>
/// Records what actually happened during one playback: how long it was watched (paused time
/// excluded), which stretches were watched, and the single vote allowed per session.
///
/// The player calls <see cref="Update"/> on a timer. Watch time advances from the wall clock
/// rather than the media position, so it stays honest if the position reporting stutters. A jump
/// between two consecutive positions that the elapsed time cannot explain is treated as a seek,
/// which closes the current region and opens a new one.
/// </summary>
public sealed class PlaybackTracker
{
    /// <summary>Below this, a session is an abandoned start and a region is not interesting.</summary>
    public const long ThresholdMs = 10_000;

    /// <summary>Position drift larger than this between ticks means the user seeked.</summary>
    private const long SeekToleranceMs = 1_500;

    private readonly Database _db;
    private readonly Func<DateTime> _now;

    private DateTime _lastTick;
    private long _lastPositionMs;
    private bool _wasPlaying;
    private bool _firstUpdate = true;

    private long _regionStartMs = -1;
    private long _regionEndMs = -1;

    public long SessionId { get; }
    public long VideoId { get; }
    public long WatchedMs { get; private set; }
    public int Vote { get; private set; }

    /// <summary>True once this session has passed the threshold and counts as a view.</summary>
    public bool Counted => WatchedMs >= ThresholdMs;

    public PlaybackTracker(Database db, long videoId, long startPositionMs = 0, Func<DateTime>? clock = null)
    {
        _db = db;
        _now = clock ?? (() => DateTime.UtcNow);
        VideoId = videoId;

        _db.Exec("INSERT INTO sessions (video_id, started_utc) VALUES ($v, $t)",
                 ("$v", videoId), ("$t", Database.Utc(_now())));
        SessionId = Convert.ToInt64(_db.Scalar("SELECT last_insert_rowid()")!);

        _lastTick = _now();
        _lastPositionMs = startPositionMs;
        _regionStartMs = startPositionMs;
        _regionEndMs = startPositionMs;
    }

    /// <summary>Feeds the tracker the current media position and whether playback is running.</summary>
    public void Update(long positionMs, bool isPlaying)
    {
        var now = _now();
        var elapsedMs = (long)(now - _lastTick).TotalMilliseconds;
        _lastTick = now;

        if (elapsedMs < 0) elapsedMs = 0;

        // The interval before the very first tick belongs to the session: playback started when
        // the session did. Later paused→playing transitions are ambiguous — we cannot tell when
        // within the interval the user resumed — so those stay conservative and count nothing.
        var playedThroughInterval = isPlaying && (_wasPlaying || _firstUpdate);
        _firstUpdate = false;

        if (playedThroughInterval)
        {
            // Only time spent actually playing counts.
            WatchedMs += elapsedMs;

            var expected = _lastPositionMs + elapsedMs;
            var drift = Math.Abs(positionMs - expected);

            if (drift > SeekToleranceMs)
            {
                FlushRegion();
                _regionStartMs = positionMs;
                _regionEndMs = positionMs;
            }
            else
            {
                _regionEndMs = positionMs;
            }
        }
        else if (isPlaying && !_wasPlaying)
        {
            // Resuming after a pause, or the very first playing tick: start a fresh region here
            // if the position moved while we were not watching.
            if (Math.Abs(positionMs - _regionEndMs) > SeekToleranceMs)
            {
                FlushRegion();
                _regionStartMs = positionMs;
            }
            _regionEndMs = positionMs;
        }

        _lastPositionMs = positionMs;
        _wasPlaying = isPlaying;
    }

    /// <summary>
    /// Records a like (+1) or dislike (-1). One vote per session: later presses are ignored,
    /// so the UI should disable both buttons once this returns true.
    /// </summary>
    public bool CastVote(int vote)
    {
        if (Vote != 0) return false;
        if (vote is not (1 or -1)) return false;

        Vote = vote;
        _db.Exec("UPDATE sessions SET vote = $v WHERE id = $id", ("$v", vote), ("$id", SessionId));
        return true;
    }

    /// <summary>Writes the region if it is long enough to say something about interest.</summary>
    private void FlushRegion()
    {
        if (_regionStartMs < 0) return;

        var start = Math.Min(_regionStartMs, _regionEndMs);
        var end = Math.Max(_regionStartMs, _regionEndMs);

        if (end - start >= ThresholdMs)
        {
            _db.Exec("""
                INSERT INTO regions (video_id, session_id, start_ms, end_ms)
                VALUES ($v, $s, $a, $b)
                """,
                ("$v", VideoId), ("$s", SessionId), ("$a", start), ("$b", end));
        }

        _regionStartMs = -1;
        _regionEndMs = -1;
    }

    /// <summary>Closes the session. A session under the threshold leaves no view behind.</summary>
    public void Finish()
    {
        FlushRegion();
        _db.Exec("""
            UPDATE sessions SET ended_utc = $t, watched_ms = $w, counted = $c WHERE id = $id
            """,
            ("$t", Database.Utc(_now())), ("$w", WatchedMs),
            ("$c", Counted ? 1 : 0), ("$id", SessionId));
    }
}

public static class Heatmap
{
    /// <summary>
    /// Turns stored regions into per-bucket replay counts normalised to 0..1. Overlapping
    /// regions add up, so a stretch watched three times reads hotter than one watched once.
    /// </summary>
    public static double[] Compute(IEnumerable<(long StartMs, long EndMs)> regions,
                                   long durationMs, int buckets)
    {
        var heat = new double[Math.Max(1, buckets)];
        if (durationMs <= 0) return heat;

        foreach (var (startMs, endMs) in regions)
        {
            var a = Math.Clamp(startMs, 0, durationMs);
            var b = Math.Clamp(endMs, 0, durationMs);
            if (b <= a) continue;

            var first = (int)(a * heat.Length / durationMs);
            var last = (int)((b - 1) * heat.Length / durationMs);
            first = Math.Clamp(first, 0, heat.Length - 1);
            last = Math.Clamp(last, 0, heat.Length - 1);

            for (var i = first; i <= last; i++) heat[i] += 1;
        }

        var max = heat.Length == 0 ? 0 : heat.Max();
        if (max > 0)
            for (var i = 0; i < heat.Length; i++) heat[i] /= max;

        return heat;
    }

    public static double[] ForVideo(Database db, long videoId, long durationMs, int buckets)
    {
        var regions = db.Query(
            "SELECT start_ms, end_ms FROM regions WHERE video_id = $v",
            r => (r.GetInt64(0), r.GetInt64(1)), ("$v", videoId));
        return Compute(regions, durationMs, buckets);
    }
}
