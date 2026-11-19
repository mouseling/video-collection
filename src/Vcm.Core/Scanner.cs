namespace Vcm.Core;

/// <summary>Walks the registered roots and reconciles what is on disk with what is in the database.</summary>
public sealed class Scanner
{
    private readonly Database _db;

    public Scanner(Database db) => _db = db;

    /// <summary>Containers worth indexing. Anything else in a root folder is ignored.</summary>
    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".flv", ".m4v", ".mpg", ".mpeg",
        ".ts", ".m2ts", ".mts", ".ogv", ".ogm", ".3gp", ".vob", ".divx", ".asf", ".rm",
        ".rmvb", ".f4v", ".mxf", ".m2v", ".qt",
    };

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public ScanResult Scan(IProgress<ScanProgress>? progress = null, CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;
        int added = 0, updated = 0, unchanged = 0, seen = 0, hashed = 0;

        var roots = _db.Query(
            "SELECT id, path FROM roots ORDER BY id",
            r => (Id: r.GetInt64(0), Path: r.GetString(1)));

        var nowIso = Database.Utc(DateTime.UtcNow);

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root.Path)) continue;

            foreach (var path in EnumerateVideos(root.Path, ct))
            {
                ct.ThrowIfCancellationRequested();
                seen++;

                FileInfo info;
                try { info = new FileInfo(path); if (!info.Exists) continue; }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                var mtime = Database.Utc(info.LastWriteTimeUtc);

                // Fast path: a known path whose size and mtime are untouched needs no hashing.
                var rows = _db.Query(
                    "SELECT id, size_bytes, mtime_utc FROM files WHERE path = $p",
                    r => (Id: r.GetInt64(0), Size: r.GetInt64(1), MTime: r.GetString(2)),
                    ("$p", path));
                var known = rows.Count > 0;
                var existing = known ? rows[0] : default;

                if (known && existing.Size == info.Length && existing.MTime == mtime)
                {
                    _db.Exec("UPDATE files SET present = 1, last_seen_utc = $t WHERE id = $id",
                             ("$t", nowIso), ("$id", existing.Id));
                    unchanged++;
                    progress?.Report(new ScanProgress(path, seen, hashed));
                    continue;
                }

                string hash;
                try { hash = ContentHasher.Compute(path); hashed++; }
                catch (IOException) { continue; }
                catch (UnauthorizedAccessException) { continue; }

                var videoId = UpsertVideo(hash, nowIso);

                if (known)
                {
                    _db.Exec("""
                        UPDATE files SET video_id = $v, root_id = $r, size_bytes = $s,
                               mtime_utc = $m, present = 1, last_seen_utc = $t
                         WHERE id = $id
                        """,
                        ("$v", videoId), ("$r", root.Id), ("$s", info.Length),
                        ("$m", mtime), ("$t", nowIso), ("$id", existing.Id));
                    updated++;
                }
                else
                {
                    _db.Exec("""
                        INSERT INTO files (video_id, root_id, path, size_bytes, mtime_utc,
                                           present, last_seen_utc)
                        VALUES ($v, $r, $p, $s, $m, 1, $t)
                        """,
                        ("$v", videoId), ("$r", root.Id), ("$p", path),
                        ("$s", info.Length), ("$m", mtime), ("$t", nowIso));
                    added++;
                }

                progress?.Report(new ScanProgress(path, seen, hashed));
            }

            _db.Exec("UPDATE roots SET last_scan_utc = $t WHERE id = $id",
                     ("$t", nowIso), ("$id", root.Id));
        }

        // Anything not touched this pass is gone from disk. The file row is dropped, but the
        // video row (and therefore its history) survives in case the file comes back.
        var removed = _db.Exec("DELETE FROM files WHERE last_seen_utc <> $t", ("$t", nowIso));

        return new ScanResult(added, updated, removed, unchanged, DateTime.UtcNow - started);
    }

    private long UpsertVideo(string hash, string nowIso)
    {
        var existing = _db.Scalar("SELECT id FROM videos WHERE content_hash = $h", ("$h", hash));
        if (existing is not null) return Convert.ToInt64(existing);

        _db.Exec("INSERT INTO videos (content_hash, first_seen_utc) VALUES ($h, $t)",
                 ("$h", hash), ("$t", nowIso));
        return Convert.ToInt64(_db.Scalar("SELECT last_insert_rowid()")!);
    }

    /// <summary>
    /// Recursive walk that survives unreadable subdirectories instead of aborting the whole scan.
    /// </summary>
    public static IEnumerable<string> EnumerateVideos(string root, CancellationToken ct = default)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { continue; }

            foreach (var sub in subdirs) stack.Push(sub);

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (UnauthorizedAccessException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            catch (IOException) { continue; }

            foreach (var f in files)
                if (IsVideo(f))
                    yield return f;
        }
    }
}
