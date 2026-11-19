using Microsoft.Data.Sqlite;

namespace Vcm.Core;

/// <summary>Everything the UI reads: roots, the folder tree, tags, and the ranked grids.</summary>
public sealed class Library
{
    private readonly Database _db;

    public Library(Database db) => _db = db;

    public Database Db => _db;

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
        try { full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
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
        _db.Exec("INSERT INTO roots (path, added_utc) VALUES ($p, $t)", ("$p", full), ("$t", now));
        var id = Convert.ToInt64(_db.Scalar("SELECT last_insert_rowid()")!);
        return new Root(id, full, Database.ParseUtc(now), null);
    }

    public void RemoveRoot(long id)
    {
        // Drop the file rows, keep the videos: their ratings and watch history stay put in case
        // the folder is registered again later.
        _db.Exec("DELETE FROM files WHERE root_id = $id", ("$id", id));
        _db.Exec("DELETE FROM roots WHERE id = $id", ("$id", id));
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
        var dir = folder.TrimEnd(Path.DirectorySeparatorChar);
        var prefix = EscapeLike(dir + Path.DirectorySeparatorChar) + "%";

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

    public IReadOnlyList<string> GetTagsForVideo(long videoId) => _db.Query("""
        SELECT t.name FROM tags t JOIN video_tags vt ON vt.tag_id = t.id
         WHERE vt.video_id = $v ORDER BY t.name COLLATE NOCASE
        """, r => r.GetString(0), ("$v", videoId));

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

    public (int Videos, int Files, long Bytes) GetLibrarySize()
    {
        var videos = Convert.ToInt32(_db.Scalar("SELECT COUNT(*) FROM videos") ?? 0);
        var files = Convert.ToInt32(_db.Scalar("SELECT COUNT(*) FROM files WHERE present = 1") ?? 0);
        var bytes = Convert.ToInt64(_db.Scalar("SELECT COALESCE(SUM(size_bytes),0) FROM files WHERE present = 1") ?? 0L);
        return (videos, files, bytes);
    }

    /// <summary>Videos that still need their duration and codecs read from the file.</summary>
    public IReadOnlyList<(long Id, string Path)> GetUnprobed(int limit = 500) => _db.Query("""
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
