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
        int added = 0, updated = 0, unchanged = 0, removed = 0, skipped = 0, seen = 0, hashed = 0;
        string? firstSkipped = null;

        var roots = _db.Query(
            "SELECT id, path FROM roots ORDER BY id",
            r => (Id: r.GetInt64(0), Path: r.GetString(1)));

        var nowIso = Database.Utc(DateTime.UtcNow);

        void Skip(string path)
        {
            skipped++;
            firstSkipped ??= path;
        }

        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();

            // A root we cannot enumerate tells us nothing about what is still on disk beneath it,
            // so it is left entirely alone rather than treated as empty.
            if (!Directory.Exists(root.Path)) { Skip(root.Path); continue; }

            // Set false by any unreadable directory. While it is false we have an incomplete
            // picture of this root, so none of its rows may be deleted as "gone".
            var walkComplete = true;

            foreach (var path in EnumerateVideos(root.Path, ct, dir => { walkComplete = false; Skip(dir); }))
            {
                ct.ThrowIfCancellationRequested();
                seen++;

                FileInfo info;
                try { info = new FileInfo(path); if (!info.Exists) continue; }
                catch (IOException) { Skip(path); Keep(path, nowIso); continue; }
                catch (UnauthorizedAccessException) { Skip(path); Keep(path, nowIso); continue; }

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
                catch (IOException) { Skip(path); Keep(path, nowIso); continue; }
                catch (UnauthorizedAccessException) { Skip(path); Keep(path, nowIso); continue; }

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

            // Scoped to THIS root, and only once its walk completed. A library-wide delete would
            // drop rows belonging to roots this pass never reached — an unmounted drive or a
            // renamed folder would silently empty the library of everything beneath it.
            if (walkComplete)
            {
                removed += _db.Exec(
                    "DELETE FROM files WHERE root_id = $r AND last_seen_utc <> $t",
                    ("$r", root.Id), ("$t", nowIso));
            }

            _db.Exec("UPDATE roots SET last_scan_utc = $t WHERE id = $id",
                     ("$t", nowIso), ("$id", root.Id));
        }

        return new ScanResult(added, updated, removed, unchanged, skipped, firstSkipped,
                              DateTime.UtcNow - started);
    }

    /// <summary>
    /// Marks an existing row as still present without re-reading the file. Used when a file
    /// cannot be examined this pass: it is almost certainly still there, and leaving it unstamped
    /// would hand it to the delete sweep.
    /// </summary>
    private void Keep(string path, string nowIso) =>
        _db.Exec("UPDATE files SET present = 1, last_seen_utc = $t WHERE path = $p",
                 ("$t", nowIso), ("$p", path));

    private long UpsertVideo(string hash, string nowIso)
    {
        var existing = _db.Scalar("SELECT id FROM videos WHERE content_hash = $h", ("$h", hash));
        if (existing is not null) return Convert.ToInt64(existing);

        return _db.InsertReturningId(
            "INSERT INTO videos (content_hash, first_seen_utc) VALUES ($h, $t)",
            ("$h", hash), ("$t", nowIso));
    }

    /// <summary>
    /// Recursive walk that survives unreadable subdirectories instead of aborting the whole scan.
    /// <paramref name="onUnreadable"/> is called with any directory that could not be listed, so
    /// the caller knows its picture of that root is incomplete and must not delete rows from it.
    /// </summary>
    public static IEnumerable<string> EnumerateVideos(string root, CancellationToken ct = default,
                                                      Action<string>? onUnreadable = null)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        // Junctions and symlinks can point back up the tree; without this a cyclic link makes
        // the walk run forever.
        var visited = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        while (stack.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = stack.Pop();

            string real;
            try { real = Directory.ResolveLinkTarget(dir, returnFinalTarget: true)?.FullName ?? dir; }
            catch (IOException) { real = dir; }
            catch (UnauthorizedAccessException) { real = dir; }

            if (!visited.Add(real)) continue;

            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch (UnauthorizedAccessException) { onUnreadable?.Invoke(dir); continue; }
            catch (DirectoryNotFoundException) { onUnreadable?.Invoke(dir); continue; }
            catch (IOException) { onUnreadable?.Invoke(dir); continue; }

            foreach (var sub in subdirs) stack.Push(sub);

            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (UnauthorizedAccessException) { onUnreadable?.Invoke(dir); continue; }
            catch (DirectoryNotFoundException) { onUnreadable?.Invoke(dir); continue; }
            catch (IOException) { onUnreadable?.Invoke(dir); continue; }

            foreach (var f in files)
                if (IsVideo(f))
                    yield return f;
        }
    }
}
