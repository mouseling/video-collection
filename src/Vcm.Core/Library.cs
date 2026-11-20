using Microsoft.Data.Sqlite;

namespace Vcm.Core;

/// <summary>Everything the UI reads: roots, the folder tree, tags, and the ranked grids.</summary>
public sealed class Library
{
    private readonly Database _db;

    public Library(Database db) => _db = db;

    // ------------------------------------------------------------------ playback

    /// <summary>
    /// Opens a viewing session for a video. Exists so the playback layer never needs a handle on
    /// the connection itself — every statement stays behind the gate that keeps the scan thread
    /// and the UI thread from colliding, and all SQL stays in this project.
    /// </summary>
    public PlaybackTracker StartSession(long videoId, long startPositionMs = 0,
                                        Func<DateTime>? clock = null) =>
        new(_db, videoId, startPositionMs, clock);

    public IReadOnlyList<(long StartMs, long EndMs)> GetRegions(long videoId) => _db.Query(
        "SELECT start_ms, end_ms FROM regions WHERE video_id = $v ORDER BY start_ms",
        r => (r.GetInt64(0), r.GetInt64(1)), ("$v", videoId));

    public double[] GetHeat(long videoId, long durationMs, int buckets) =>
        Heatmap.Compute(GetRegions(videoId), durationMs, buckets);

    /// <summary>Like and dislike totals for one video, without the full-library aggregate cost.</summary>
    public (int Likes, int Dislikes) GetVoteCounts(long videoId) => _db.Query("""
        SELECT SUM(CASE WHEN vote =  1 THEN 1 ELSE 0 END),
               SUM(CASE WHEN vote = -1 THEN 1 ELSE 0 END)
          FROM sessions WHERE video_id = $v AND vote <> 0
        """,
        r => (r.IsDBNull(0) ? 0 : r.GetInt32(0), r.IsDBNull(1) ? 0 : r.GetInt32(1)),
        ("$v", videoId)).FirstOrDefault();

    // ------------------------------------------------------------------ roots

    public IReadOnlyList<Root> GetRoots() => _db.Query(
        "SELECT id, path, added_utc, last_scan_utc FROM roots ORDER BY path",
        r => new Root(r.GetInt64(0), r.GetString(1), Database.ParseUtc(r.GetString(2)),
                      Database.ParseUtcOrNull(r.IsDBNull(3) ? null : r.GetString(3))));

    /// <summary>
    /// Registers a folder. Returns null if the path does not exist, is already registered, or
    /// sits inside a root that is already registered — nesting roots would double-count files.
    /// </summary>
    public Root? AddRoot(string path, out string? error)
    {
        error = null;
        string full;
        try { full = Normalise(path); }
        catch (Exception ex) { error = $"Not a usable path: {ex.Message}"; return null; }

        if (!Directory.Exists(full)) { error = "That folder does not exist."; return null; }

        foreach (var existing in GetRoots())
        {
            if (string.Equals(existing.Path, full, PathComparison))
            {
                error = "That folder is already a root.";
                return null;
            }
            if (IsInside(full, existing.Path))
            {
                error = $"Already covered by the root {existing.Path}.";
                return null;
            }
            if (IsInside(existing.Path, full))
            {
                error = $"That folder contains the existing root {existing.Path}. Remove it first.";
                return null;
            }
        }

        var now = Database.Utc(DateTime.UtcNow);
        var id = _db.InsertReturningId(
            "INSERT INTO roots (path, added_utc) VALUES ($p, $t)", ("$p", full), ("$t", now));
        return new Root(id, full, Database.ParseUtc(now), null);
    }

    public void RemoveRoot(long id)
    {
        // Drop the file rows, keep the videos: their ratings and watch history stay put in case
        // the folder is registered again later.
        _db.Exec("DELETE FROM files WHERE root_id = $id", ("$id", id));
        _db.Exec("DELETE FROM roots WHERE id = $id", ("$id", id));
    }

    /// <summary>
    /// Canonicalises a root path. The trailing separator is stripped so comparisons and the
    /// LIKE prefix in <see cref="GetVideosInFolder"/> behave, EXCEPT for a drive root: trimming
    /// "D:\" to "D:" changes its meaning on Windows from the root of drive D to the current
    /// directory on drive D, which would scan the wrong place entirely.
    /// </summary>
    private static string Normalise(string path)
    {
        var full = Path.GetFullPath(path);

        if (Path.GetPathRoot(full) is { Length: > 0 } root &&
            string.Equals(full, root, PathComparison))
            return full;

        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static bool IsInside(string child, string parent) =>
        child.StartsWith(parent + Path.DirectorySeparatorChar, PathComparison);

    // ------------------------------------------------------------------ folder tree

    /// <summary>Builds the Locations tree, mirroring the on-disk folder structure under each root.</summary>
    public List<FolderNode> GetFolderTree()
    {
        var roots = GetRoots();
        var nodes = new Dictionary<string, FolderNode>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var result = new List<FolderNode>();

        foreach (var root in roots)
        {
            var node = new FolderNode
            {
                Name = root.Path,
                FullPath = root.Path,
                IsRoot = true,
                RootId = root.Id,
            };
            nodes[root.Path] = node;
            result.Add(node);
        }

        var files = _db.Query(
            "SELECT r.path, f.path FROM files f JOIN roots r ON r.id = f.root_id WHERE f.present = 1",
            r => (RootPath: r.GetString(0), FilePath: r.GetString(1)));

        foreach (var (rootPath, filePath) in files)
        {
            if (!nodes.TryGetValue(rootPath, out var rootNode)) continue;

            var dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(dir)) continue;

            var node = EnsureFolder(nodes, rootNode, rootPath, dir);
            node.DirectCount++;

            // Roll the file up through every ancestor so folder totals include subfolders.
            for (var walk = node; walk is not null; walk = ParentOf(nodes, rootPath, walk))
                walk.TotalCount++;
        }

        foreach (var node in nodes.Values)
            node.Children.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

        result.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        return result;
    }

    private static FolderNode? ParentOf(Dictionary<string, FolderNode> nodes, string rootPath, FolderNode node)
    {
        if (string.Equals(node.FullPath, rootPath, PathComparison)) return null;
        var parent = Path.GetDirectoryName(node.FullPath);
        if (string.IsNullOrEmpty(parent)) return null;
        return nodes.TryGetValue(parent, out var p) ? p : null;
    }

    private static FolderNode EnsureFolder(Dictionary<string, FolderNode> nodes, FolderNode rootNode,
                                           string rootPath, string dir)
    {
        if (nodes.TryGetValue(dir, out var found)) return found;

        var parentPath = Path.GetDirectoryName(dir);
        FolderNode parent = string.IsNullOrEmpty(parentPath) ||
                            string.Equals(dir, rootPath, PathComparison)
            ? rootNode
            : EnsureFolder(nodes, rootNode, rootPath, parentPath);

        var node = new FolderNode
        {
            Name = Path.GetFileName(dir) is { Length: > 0 } n ? n : dir,
            FullPath = dir,
            RootId = rootNode.RootId,
        };
        nodes[dir] = node;
        parent.Children.Add(node);
        return node;
    }

    // ------------------------------------------------------------------ grids

    /// <summary>
    /// Watch statistics come only from sessions that passed the 10-second threshold. Votes are
    /// counted from any session, because pressing like is a deliberate act regardless of length.
    /// </summary>
    private const string StatJoins = """
        LEFT JOIN (SELECT video_id,
                          SUM(watched_ms) AS total_ms,
                          COUNT(*)        AS views
                     FROM sessions WHERE counted = 1 GROUP BY video_id) w ON w.video_id = v.id
        LEFT JOIN (SELECT video_id,
                          SUM(CASE WHEN vote =  1 THEN 1 ELSE 0 END) AS likes,
                          SUM(CASE WHEN vote = -1 THEN 1 ELSE 0 END) AS dislikes
                     FROM sessions WHERE vote <> 0 GROUP BY video_id) vt ON vt.video_id = v.id
        LEFT JOIN (SELECT video_id, COUNT(*) AS cnt FROM files GROUP BY video_id) fc ON fc.video_id = v.id
        LEFT JOIN (SELECT vt2.video_id, GROUP_CONCAT(tg.name, ', ') AS tags
                     FROM video_tags vt2 JOIN tags tg ON tg.id = vt2.tag_id
                    GROUP BY vt2.video_id) tl ON tl.video_id = v.id
        """;

    private const string StatCols = """
        v.id, f.path, v.duration_ms,
        COALESCE(w.total_ms, 0), COALESCE(w.views, 0),
        COALESCE(vt.likes, 0), COALESCE(vt.dislikes, 0),
        COALESCE(fc.cnt, 0), COALESCE(tl.tags, '')
        """;

    private static VideoStats MapStats(SqliteDataReader r)
    {
        var path = r.GetString(1);
        return new VideoStats(
            VideoId: r.GetInt64(0),
            DisplayName: Path.GetFileName(path),
            Path: path,
            Folder: Path.GetDirectoryName(path) ?? "",
            DurationMs: r.GetInt64(2),
            TotalWatchedMs: r.GetInt64(3),
            Views: r.GetInt32(4),
            Likes: r.GetInt32(5),
            Dislikes: r.GetInt32(6),
            FileCount: r.GetInt32(7),
            Tags: r.GetString(8));
    }

    /// <summary>
    /// One row per file on disk — used by the Locations tree. Everything under <paramref name="folder"/>
    /// is fetched with a prefix match; a non-recursive listing then keeps only that folder's own
    /// files, since SQLite has no dirname() to filter on directly.
    /// </summary>
    public IReadOnlyList<VideoStats> GetVideosInFolder(string folder, bool recursive = false)
    {
        // A drive root already ends in a separator; trimming and re-appending would be wrong
        // for it and right for everything else, so keep whatever Normalise settled on.
        var dir = Normalise(folder);
        var withSeparator = dir.EndsWith(Path.DirectorySeparatorChar)
            ? dir
            : dir + Path.DirectorySeparatorChar;
        var prefix = EscapeLike(withSeparator) + "%";

        var rows = _db.Query($"""
            SELECT {StatCols}
              FROM files f
              JOIN videos v ON v.id = f.video_id
              {StatJoins}
             WHERE f.present = 1 AND f.path LIKE $prefix ESCAPE '\'
             ORDER BY f.path COLLATE NOCASE
            """, MapStats, ("$prefix", prefix));

        return recursive
            ? rows
            : rows.Where(v => string.Equals(v.Folder, dir, PathComparison)).ToList();
    }

    /// <summary>
    /// Escapes LIKE wildcards. Paired with <c>ESCAPE '\'</c> at every call site — without that
    /// clause SQLite treats a backslash literally, which would break every Windows path.
    /// </summary>
    private static string EscapeLike(string s) =>
        s.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");

    /// <summary>
    /// Searches every registered root. Matches anywhere in the full path, so a folder name finds
    /// everything beneath it as readily as a file name finds one video.
    /// </summary>
    public IReadOnlyList<VideoStats> Search(string term, int limit = 1000)
    {
        term = term.Trim();
        if (term.Length == 0) return [];

        return _db.Query($"""
            SELECT {StatCols}
              FROM files f
              JOIN videos v ON v.id = f.video_id
              {StatJoins}
             WHERE f.present = 1 AND f.path LIKE $q ESCAPE '\'
             ORDER BY f.path COLLATE NOCASE
             LIMIT $n
            """, MapStats, ("$q", "%" + EscapeLike(term) + "%"), ("$n", limit));
    }

    /// <summary>
    /// Renames a file on disk and follows it in the index.
    ///
    /// Statistics need no special handling: identity is the content hash, and the file row keeps
    /// pointing at the same video, so ratings, watch time and tags come along by construction.
    /// </summary>
    public bool RenameFile(string path, string newName, out string? error)
    {
        error = null;
        newName = newName.Trim();

        if (newName.Length == 0) { error = "The name is empty."; return false; }
        if (newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "That name contains characters a file name cannot have.";
            return false;
        }

        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) { error = "That path is not usable."; return false; }
        if (!File.Exists(path)) { error = "That file is no longer on disk."; return false; }

        var target = Path.Combine(dir, newName);
        if (string.Equals(target, path, PathComparison)) return true;
        if (File.Exists(target)) { error = "A file with that name already exists here."; return false; }

        try
        {
            File.Move(path, target);
        }
        catch (Exception ex)
        {
            // Most often the file is open in the player, or read-only.
            error = ex.Message;
            return false;
        }

        _db.Exec("UPDATE files SET path = $new WHERE path = $old", ("$new", target), ("$old", path));
        return true;
    }

    /// <summary>One row per distinct video — used by Tags, Rating and Most viewed.</summary>
    private IReadOnlyList<VideoStats> QueryByVideo(string where, string orderBy,
                                                   params (string, object?)[] args) =>
        _db.Query($"""
            SELECT {StatCols}
              FROM videos v
              JOIN files f ON f.video_id = v.id
                          AND f.id = (SELECT MIN(id) FROM files WHERE video_id = v.id AND present = 1)
              {StatJoins}
             {where}
             ORDER BY {orderBy}
            """, MapStats, args);

    public IReadOnlyList<VideoStats> GetRating(RatingSort sort) => QueryByVideo(
        "WHERE COALESCE(vt.likes,0) + COALESCE(vt.dislikes,0) > 0",
        sort switch
        {
            RatingSort.NetScore => "(COALESCE(vt.likes,0) - COALESCE(vt.dislikes,0)) DESC, COALESCE(vt.likes,0) DESC",
            RatingSort.Ratio => "CAST(COALESCE(vt.likes,0) AS REAL) / (COALESCE(vt.likes,0) + COALESCE(vt.dislikes,0)) DESC, COALESCE(vt.likes,0) DESC",
            RatingSort.MostLiked => "COALESCE(vt.likes,0) DESC",
            RatingSort.MostDisliked => "COALESCE(vt.dislikes,0) DESC",
            _ => "1",
        });

    public IReadOnlyList<VideoStats> GetMostViewed(ViewedSort sort) => QueryByVideo(
        "WHERE COALESCE(w.views,0) > 0",
        sort == ViewedSort.TotalTime
            ? "COALESCE(w.total_ms,0) DESC, COALESCE(w.views,0) DESC"
            : "COALESCE(w.views,0) DESC, COALESCE(w.total_ms,0) DESC");

    public IReadOnlyList<VideoStats> GetAllVideos() =>
        QueryByVideo("", "f.path COLLATE NOCASE");

    public IReadOnlyList<VideoStats> GetVideosByTag(long tagId) => QueryByVideo(
        "WHERE EXISTS (SELECT 1 FROM video_tags x WHERE x.video_id = v.id AND x.tag_id = $tag)",
        "f.path COLLATE NOCASE", ("$tag", tagId));

    /// <summary>Videos whose content exists at more than one path.</summary>
    public IReadOnlyList<(VideoStats Video, IReadOnlyList<string> Paths)> GetDuplicates()
    {
        var dupes = QueryByVideo("WHERE COALESCE(fc.cnt,0) > 1", "f.path COLLATE NOCASE");
        var result = new List<(VideoStats, IReadOnlyList<string>)>();
        foreach (var v in dupes)
        {
            var paths = _db.Query(
                "SELECT path FROM files WHERE video_id = $v AND present = 1 ORDER BY path",
                r => r.GetString(0), ("$v", v.VideoId));
            result.Add((v, paths));
        }
        return result;
    }

    public VideoStats? GetVideo(long videoId) =>
        QueryByVideo("WHERE v.id = $id", "1", ("$id", videoId)).FirstOrDefault();

    // ------------------------------------------------------------------ tags

    public IReadOnlyList<Tag> GetTags() => _db.Query("""
        SELECT t.id, t.name, COUNT(vt.video_id)
          FROM tags t LEFT JOIN video_tags vt ON vt.tag_id = t.id
         GROUP BY t.id, t.name
         ORDER BY t.name COLLATE NOCASE
        """, r => new Tag(r.GetInt64(0), r.GetString(1), r.GetInt32(2)));

    /// <summary>The tags on one video, with ids so a tag can be taken off again.</summary>
    public IReadOnlyList<Tag> GetTagsForVideo(long videoId) => _db.Query("""
        SELECT t.id, t.name FROM tags t JOIN video_tags vt ON vt.tag_id = t.id
         WHERE vt.video_id = $v ORDER BY t.name COLLATE NOCASE
        """, r => new Tag(r.GetInt64(0), r.GetString(1), 0), ("$v", videoId));

    /// <summary>Attaches a tag, creating it if this is the first use. Returns the tag id.</summary>
    public long AddTag(long videoId, string name)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Tag name is empty.", nameof(name));

        _db.Exec("INSERT OR IGNORE INTO tags (name) VALUES ($n)", ("$n", name));
        var tagId = Convert.ToInt64(_db.Scalar("SELECT id FROM tags WHERE name = $n", ("$n", name))!);
        _db.Exec("INSERT OR IGNORE INTO video_tags (video_id, tag_id, added_utc) VALUES ($v, $t, $d)",
                 ("$v", videoId), ("$t", tagId), ("$d", Database.Utc(DateTime.UtcNow)));
        return tagId;
    }

    public void RemoveTag(long videoId, long tagId) =>
        _db.Exec("DELETE FROM video_tags WHERE video_id = $v AND tag_id = $t",
                 ("$v", videoId), ("$t", tagId));

    /// <summary>Deletes tags that are no longer attached to anything.</summary>
    public int PruneUnusedTags() => _db.Exec(
        "DELETE FROM tags WHERE id NOT IN (SELECT tag_id FROM video_tags)");

    // ------------------------------------------------------------------ misc

    /// <summary>
    /// Totals for the sidebar. Videos are counted only where a file is still on disk: a video
    /// whose files have all gone keeps its history but appears in no grid, so counting it here
    /// would report "47 videos" above an empty library.
    /// </summary>
    public (int Videos, int Files, long Bytes) GetLibrarySize()
    {
        var videos = Convert.ToInt32(_db.Scalar(
            "SELECT COUNT(DISTINCT video_id) FROM files WHERE present = 1") ?? 0);
        var files = Convert.ToInt32(_db.Scalar("SELECT COUNT(*) FROM files WHERE present = 1") ?? 0);
        var bytes = Convert.ToInt64(_db.Scalar("SELECT COALESCE(SUM(size_bytes),0) FROM files WHERE present = 1") ?? 0L);
        return (videos, files, bytes);
    }

    /// <summary>
    /// Videos that still need their duration and codecs read from the file. Returned in batches
    /// so a first scan of a large collection reports progress and stays cancellable; callers are
    /// expected to loop until this comes back empty.
    /// </summary>
    public const int ProbeBatchSize = 500;

    public IReadOnlyList<(long Id, string Path)> GetUnprobed(int limit = ProbeBatchSize) => _db.Query("""
        SELECT v.id, f.path
          FROM videos v
          JOIN files f ON f.video_id = v.id
                      AND f.id = (SELECT MIN(id) FROM files WHERE video_id = v.id AND present = 1)
         WHERE v.probed = 0
         LIMIT $n
        """, r => (r.GetInt64(0), r.GetString(1)), ("$n", limit));

    public void SaveProbe(long videoId, long durationMs, int width, int height,
                          string? videoCodec, string? audioCodec) =>
        _db.Exec("""
            UPDATE videos SET duration_ms = $d, width = $w, height = $h,
                   video_codec = $vc, audio_codec = $ac, probed = 1
             WHERE id = $id
            """,
            ("$d", durationMs), ("$w", width), ("$h", height),
            ("$vc", videoCodec), ("$ac", audioCodec), ("$id", videoId));
}
