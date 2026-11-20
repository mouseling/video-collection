using Vcm.Core;

namespace Vcm.Cli;

/// <summary>
/// Headless driver for the core library. Exists so scanning, hashing, duplicate detection and
/// the statistics rules can be exercised and verified without going through the UI.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return 1; }

        var dbPath = Environment.GetEnvironmentVariable("VCM_DB") ?? "vcm-test.db";
        using var db = new Database(dbPath);
        var lib = new Library(db);

        try
        {
            switch (args[0])
            {
                case "add-root": return AddRoot(lib, args);
                case "roots": return ShowRoots(lib);
                case "scan": return Scan(db);
                case "tree": return ShowTree(lib);
                case "ls": return ShowFolder(lib, args);
                case "dupes": return ShowDupes(lib);
                case "tag": return AddTag(lib, args);
                case "tags": return ShowTags(lib);
                case "by-tag": return ShowByTag(lib, args);
                case "rating": return ShowRating(lib, args);
                case "viewed": return ShowViewed(lib, args);
                case "simulate": return Simulate(db, lib, args);
                case "heat": return ShowHeat(db, lib, args);
                case "stats": return ShowStats(lib);
                case "probe": return Probe(lib).GetAwaiter().GetResult();
                case "probe-one": return ProbeOne(args).GetAwaiter().GetResult();
                case "playtest": return PlayTest(lib, args).GetAwaiter().GetResult();
                default: Usage(); return 1;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static void Usage() => Console.WriteLine("""
        vcm <command>   (database from $VCM_DB, default ./vcm-test.db)

          add-root <path>          register a folder
          roots                    list roots
          scan                     walk roots, hash new/changed files
          tree                     folder tree with counts
          ls <folder> [-r]         videos in a folder
          dupes                    same content at more than one path
          tag <videoId> <name>     attach a tag
          tags                     all tags with counts
          by-tag <name>            videos carrying a tag
          rating [net|ratio|liked|disliked]
          viewed [time|views]
          simulate <videoId> <watchSec> [startSec] [vote]
                                   fake a viewing session
          heat <videoId> [buckets] replay heat as a bar chart
          stats                    library totals
        """);

    // ------------------------------------------------------------------ commands

    private static int AddRoot(Library lib, string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("need a path"); return 1; }
        var root = lib.AddRoot(args[1], out var error);
        if (root is null) { Console.Error.WriteLine(error); return 1; }
        Console.WriteLine($"added root {root.Id}: {root.Path}");
        return 0;
    }

    private static int ShowRoots(Library lib)
    {
        foreach (var r in lib.GetRoots())
            Console.WriteLine($"  {r.Id,3}  {r.Path}   (scanned: {r.LastScanUtc?.ToString("u") ?? "never"})");
        return 0;
    }

    private static int Scan(Database db)
    {
        var scanner = new Scanner(db);
        var result = scanner.Scan();
        Console.WriteLine($"added {result.Added}, updated {result.Updated}, " +
                          $"removed {result.Removed}, unchanged {result.Unchanged} " +
                          $"in {result.Elapsed.TotalSeconds:0.00}s");
        return 0;
    }

    private static int ShowTree(Library lib)
    {
        void Print(FolderNode n, int depth)
        {
            var pad = new string(' ', depth * 2);
            Console.WriteLine($"  {pad}{n.Name}   [{n.DirectCount} here / {n.TotalCount} total]");
            foreach (var c in n.Children) Print(c, depth + 1);
        }
        foreach (var root in lib.GetFolderTree()) Print(root, 0);
        return 0;
    }

    private static int ShowFolder(Library lib, string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("need a folder"); return 1; }
        var recursive = args.Contains("-r");
        foreach (var v in lib.GetVideosInFolder(Path.GetFullPath(args[1]), recursive))
            Console.WriteLine($"  {v.VideoId,3}  {Fmt.Time(v.DurationMs),8}  {v.DisplayName}" +
                              (v.IsDuplicated ? $"   [{v.FileCount} copies]" : ""));
        return 0;
    }

    private static int ShowDupes(Library lib)
    {
        var dupes = lib.GetDuplicates();
        if (dupes.Count == 0) { Console.WriteLine("  no duplicates"); return 0; }
        foreach (var (video, paths) in dupes)
        {
            Console.WriteLine($"  video {video.VideoId}: {paths.Count} copies");
            foreach (var p in paths) Console.WriteLine($"      {p}");
        }
        return 0;
    }

    private static int AddTag(Library lib, string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("need <videoId> <name>"); return 1; }
        lib.AddTag(long.Parse(args[1]), args[2]);
        Console.WriteLine($"tagged video {args[1]} with '{args[2]}'");
        return 0;
    }

    private static int ShowTags(Library lib)
    {
        foreach (var t in lib.GetTags()) Console.WriteLine($"  {t.Id,3}  {t.Name,-20} {t.VideoCount}");
        return 0;
    }

    private static int ShowByTag(Library lib, string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("need a tag name"); return 1; }
        var tag = lib.GetTags().FirstOrDefault(t =>
            string.Equals(t.Name, args[1], StringComparison.OrdinalIgnoreCase));
        if (tag is null) { Console.Error.WriteLine("no such tag"); return 1; }
        foreach (var v in lib.GetVideosByTag(tag.Id))
            Console.WriteLine($"  {v.VideoId,3}  {v.DisplayName}");
        return 0;
    }

    private static int ShowRating(Library lib, string[] args)
    {
        var sort = args.Length > 1 ? args[1] : "net";
        var mode = sort switch
        {
            "ratio" => RatingSort.Ratio,
            "liked" => RatingSort.MostLiked,
            "disliked" => RatingSort.MostDisliked,
            _ => RatingSort.NetScore,
        };
        Console.WriteLine($"  sorted by {mode}");
        foreach (var v in lib.GetRating(mode))
            Console.WriteLine($"  {v.VideoId,3}  +{v.Likes,-3} -{v.Dislikes,-3} " +
                              $"score {v.Score,3}  ratio {v.Ratio * 100,3:0}%   {v.DisplayName}");
        return 0;
    }

    private static int ShowViewed(Library lib, string[] args)
    {
        var mode = args.Length > 1 && args[1] == "views" ? ViewedSort.ViewCount : ViewedSort.TotalTime;
        Console.WriteLine($"  sorted by {mode}");
        foreach (var v in lib.GetMostViewed(mode))
            Console.WriteLine($"  {v.VideoId,3}  {Fmt.Time(v.TotalWatchedMs),9} total  " +
                              $"{v.Views,3} views   {v.DisplayName}");
        return 0;
    }

    /// <summary>
    /// Fakes a viewing session against a virtual clock so the 10-second rule, paused-time
    /// exclusion and region capture can be checked without waiting in real time.
    /// </summary>
    private static int Simulate(Database db, Library lib, string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("need <videoId> <watchSec> [startSec] [vote]"); return 1; }

        var videoId = long.Parse(args[1]);
        var watchSec = int.Parse(args[2]);
        var startSec = args.Length > 3 ? int.Parse(args[3]) : 0;
        var vote = args.Length > 4 ? int.Parse(args[4]) : 0;

        var clock = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var tracker = new PlaybackTracker(db, videoId, startSec * 1000L, () => clock);

        if (vote != 0) tracker.CastVote(vote);

        // Tick once a second, exactly as the player will.
        var position = startSec * 1000L;
        for (var i = 0; i < watchSec; i++)
        {
            clock = clock.AddSeconds(1);
            position += 1000;
            tracker.Update(position, isPlaying: true);
        }
        tracker.Finish();

        Console.WriteLine($"  session {tracker.SessionId}: watched {tracker.WatchedMs / 1000}s, " +
                          $"counted={tracker.Counted}, vote={tracker.Vote}");
        return 0;
    }

    private static int ShowHeat(Database db, Library lib, string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("need <videoId>"); return 1; }
        var videoId = long.Parse(args[1]);
        var buckets = args.Length > 2 ? int.Parse(args[2]) : 60;

        var video = lib.GetVideo(videoId);
        if (video is null) { Console.Error.WriteLine("no such video"); return 1; }

        var duration = video.DurationMs > 0 ? video.DurationMs : 60 * 60 * 1000;
        var heat = Heatmap.ForVideo(db, videoId, duration, buckets);

        const string ramp = " .:-=+*#%@";
        Console.WriteLine($"  {video.DisplayName}  ({Fmt.Time(duration)})");
        Console.Write("  ");
        foreach (var h in heat) Console.Write(ramp[Math.Clamp((int)(h * (ramp.Length - 1)), 0, ramp.Length - 1)]);
        Console.WriteLine();

        var regions = db.Query("SELECT start_ms, end_ms FROM regions WHERE video_id = $v ORDER BY start_ms",
                               r => (r.GetInt64(0), r.GetInt64(1)), ("$v", videoId));
        Console.WriteLine($"  {regions.Count} regions:");
        foreach (var (a, b) in regions) Console.WriteLine($"      {Fmt.Time(a)} → {Fmt.Time(b)}");
        return 0;
    }

    /// <summary>
    /// Plays a video for real through libvlc for a few seconds, optionally seeking first, then
    /// reports what the tracker persisted. End-to-end proof that playback and statistics agree.
    /// </summary>
    private static async Task<int> PlayTest(Library lib, string[] args)
    {
        if (args.Length < 3) { Console.Error.WriteLine("need <videoId> <seconds> [seekSec]"); return 1; }

        var videoId = long.Parse(args[1]);
        var seconds = int.Parse(args[2]);
        var seekSec = args.Length > 3 ? int.Parse(args[3]) : -1;

        var vlc = Vcm.Media.VlcRuntime.Instance;
        if (vlc is null) { Console.Error.WriteLine($"libvlc unavailable: {Vcm.Media.VlcRuntime.Failure}"); return 1; }

        var video = lib.GetVideo(videoId);
        if (video is null) { Console.Error.WriteLine("no such video"); return 1; }

        using var engine = new Vcm.Media.PlayerEngine(vlc, lib);
        Console.WriteLine($"  playing {video.DisplayName} for {seconds}s" +
                          (seekSec >= 0 ? $" (seeking to {seekSec}s first)" : ""));
        engine.Play(video);

        await Task.Delay(1500);   // let it actually open and start decoding
        if (seekSec >= 0 && video.DurationMs > 0)
        {
            engine.SeekFraction(seekSec * 1000.0 / video.DurationMs);
            await Task.Delay(500);
        }

        await Task.Delay(seconds * 1000);
        Console.WriteLine($"  position reached {Fmt.Time(engine.PositionMs)} / {Fmt.Time(engine.DurationMs)}");
        engine.Stop();
        await Task.Delay(300);

        var after = lib.GetVideo(videoId)!;
        Console.WriteLine($"  recorded: {after.Views} views, {Fmt.Time(after.TotalWatchedMs)} total watched");
        return 0;
    }

    /// <summary>Probes a single file, for isolating playback problems.</summary>
    private static async Task<int> ProbeOne(string[] args)
    {
        if (args.Length < 2) { Console.Error.WriteLine("need a path"); return 1; }

        var vlc = Vcm.Media.VlcRuntime.Instance;
        if (vlc is null)
        {
            Console.Error.WriteLine($"libvlc unavailable: {Vcm.Media.VlcRuntime.Failure}");
            return 1;
        }

        var probe = new Vcm.Media.MediaProbe(vlc);
        foreach (var path in args.Skip(1))
        {
            var r = await probe.ProbeAsync(path);
            Console.WriteLine(r is null
                ? $"  FAILED   {Path.GetFileName(path)}"
                : $"  {Fmt.Time(r.DurationMs),9}  {r.Width}x{r.Height,-5} {r.VideoCodec}/{r.AudioCodec}  {Path.GetFileName(path)}");
        }
        return 0;
    }

    /// <summary>Reads duration, resolution and codecs for everything not yet probed.</summary>
    private static async Task<int> Probe(Library lib)
    {
        var vlc = Vcm.Media.VlcRuntime.Instance;
        if (vlc is null)
        {
            Console.Error.WriteLine($"libvlc unavailable: {Vcm.Media.VlcRuntime.Failure}");
            return 1;
        }

        var probe = new Vcm.Media.MediaProbe(vlc);
        var run = await probe.ProbePendingAsync(lib);
        Console.WriteLine($"  probed {run.Probed}" +
                          (run.Unreadable > 0 ? $", {run.Unreadable} unreadable" : "") +
                          (run.Failed > 0 ? $", {run.Failed} failed (left for a later scan)" : ""));

        foreach (var v in lib.GetAllVideos())
            Console.WriteLine($"  {v.VideoId,3}  {Fmt.Time(v.DurationMs),9}  {v.DisplayName}");
        return 0;
    }

    private static int ShowStats(Library lib)
    {
        var (videos, files, bytes) = lib.GetLibrarySize();
        Console.WriteLine($"  {videos} videos, {files} files, {Fmt.Size(bytes)}");
        return 0;
    }
}
