using Microsoft.Data.Sqlite;

namespace Vcm.Core;

/// <summary>
/// Owns the SQLite connection and the schema. One connection, guarded by <see cref="Gate"/>,
/// because scanning runs on a background thread while the UI reads on its own.
/// </summary>
public sealed class Database : IDisposable
{
    public const int SchemaVersion = 1;

    /// <summary>
    /// The one connection. Private on purpose: every statement must go through the helpers below
    /// so it runs under <see cref="_gate"/>. Handing this out would let a caller run unsynchronised
    /// SQL and quietly void the guarantee that keeps the scanner and the UI thread apart.
    /// </summary>
    private readonly SqliteConnection _connection;
    private readonly object _gate = new();
    private bool _disposed;

    public string Path { get; }

    public Database(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
        }.ToString());
        _connection.Open();

        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        // A second instance, or the CLI run against the same library, otherwise surfaces as an
        // immediate unhandled SQLITE_BUSY instead of a short wait.
        Exec("PRAGMA busy_timeout=5000;");
        Migrate();
    }

    private void Migrate()
    {
        var current = Convert.ToInt32(Scalar("PRAGMA user_version;") ?? 0);

        // A database written by a newer build must not be opened by an older one. Velopack makes
        // this reachable: the folder is portable, so an older copy can be unzipped over a library
        // that has already been migrated. Failing loudly here beats failing on a missing column
        // somewhere deep in a query.
        if (current > SchemaVersion)
            throw new InvalidOperationException(
                $"'{System.IO.Path.GetFileName(Path)}' was written by a newer version of this " +
                $"application (database schema {current}, this build understands {SchemaVersion}). " +
                "Update the application to open it.");

        if (current == SchemaVersion) return;

        if (current < 1)
        {
            Exec(Schema);
            Exec($"PRAGMA user_version={SchemaVersion};");
        }
    }

    /// <summary>
    /// Takes a consistent point-in-time copy. VACUUM INTO is used rather than a file copy
    /// because it accounts for the write-ahead log — copying library.db alone while the app is
    /// running yields a database missing the most recent transactions.
    /// </summary>
    public void BackupIfStale(TimeSpan maxAge)
    {
        var target = Path + ".bak";
        try
        {
            if (File.Exists(target) && DateTime.UtcNow - File.GetLastWriteTimeUtc(target) < maxAge)
                return;

            var temp = target + ".tmp";
            if (File.Exists(temp)) File.Delete(temp);

            Exec($"VACUUM INTO '{temp.Replace("'", "''")}';");
            File.Move(temp, target, overwrite: true);
        }
        catch (Exception)
        {
            // A backup is best-effort. A full disk or a read-only location must not stop the
            // application from opening the library it is backing up.
        }
    }

    private const string Schema = """
        CREATE TABLE roots (
            id            INTEGER PRIMARY KEY,
            path          TEXT    NOT NULL UNIQUE,
            added_utc     TEXT    NOT NULL,
            last_scan_utc TEXT
        );

        -- Identity is the content hash, never the path. Moving or renaming a file keeps its history.
        CREATE TABLE videos (
            id             INTEGER PRIMARY KEY,
            content_hash   TEXT    NOT NULL UNIQUE,
            duration_ms    INTEGER NOT NULL DEFAULT 0,
            width          INTEGER NOT NULL DEFAULT 0,
            height         INTEGER NOT NULL DEFAULT 0,
            video_codec    TEXT,
            audio_codec    TEXT,
            probed         INTEGER NOT NULL DEFAULT 0,
            first_seen_utc TEXT    NOT NULL
        );
        CREATE INDEX idx_videos_hash ON videos(content_hash);

        -- Several rows per video == duplicate copies on disk.
        CREATE TABLE files (
            id            INTEGER PRIMARY KEY,
            video_id      INTEGER NOT NULL REFERENCES videos(id) ON DELETE CASCADE,
            root_id       INTEGER          REFERENCES roots(id)  ON DELETE SET NULL,
            path          TEXT    NOT NULL UNIQUE,
            size_bytes    INTEGER NOT NULL,
            mtime_utc     TEXT    NOT NULL,
            present       INTEGER NOT NULL DEFAULT 1,
            last_seen_utc TEXT    NOT NULL
        );
        CREATE INDEX idx_files_video ON files(video_id);
        CREATE INDEX idx_files_root  ON files(root_id);

        CREATE TABLE tags (
            id   INTEGER PRIMARY KEY,
            name TEXT NOT NULL UNIQUE COLLATE NOCASE
        );

        CREATE TABLE video_tags (
            video_id  INTEGER NOT NULL REFERENCES videos(id) ON DELETE CASCADE,
            tag_id    INTEGER NOT NULL REFERENCES tags(id)   ON DELETE CASCADE,
            added_utc TEXT    NOT NULL,
            PRIMARY KEY (video_id, tag_id)
        );

        -- One row per playback. The vote lives here, which is what enforces one vote per session.
        CREATE TABLE sessions (
            id           INTEGER PRIMARY KEY,
            video_id     INTEGER NOT NULL REFERENCES videos(id) ON DELETE CASCADE,
            started_utc  TEXT    NOT NULL,
            ended_utc    TEXT,
            watched_ms   INTEGER NOT NULL DEFAULT 0,
            counted      INTEGER NOT NULL DEFAULT 0,
            vote         INTEGER NOT NULL DEFAULT 0
        );
        CREATE INDEX idx_sessions_video ON sessions(video_id);

        -- Stretches actually watched. Only written once they pass the threshold.
        CREATE TABLE regions (
            id         INTEGER PRIMARY KEY,
            video_id   INTEGER NOT NULL REFERENCES videos(id)   ON DELETE CASCADE,
            session_id INTEGER NOT NULL REFERENCES sessions(id) ON DELETE CASCADE,
            start_ms   INTEGER NOT NULL,
            end_ms     INTEGER NOT NULL
        );
        CREATE INDEX idx_regions_video ON regions(video_id);

        CREATE TABLE settings (
            key   TEXT PRIMARY KEY,
            value TEXT NOT NULL
        );
        """;

    // ---------------------------------------------------------------- helpers

    private SqliteCommand Cmd(string sql, (string Name, object? Value)[] args)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    public int Exec(string sql, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Cmd(sql, args);
            return cmd.ExecuteNonQuery();
        }
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Cmd(sql, args);
            var v = cmd.ExecuteScalar();
            return v == DBNull.Value ? null : v;
        }
    }

    /// <summary>
    /// Inserts a row and returns its id in ONE statement.
    ///
    /// Doing this as an INSERT followed by a separate SELECT last_insert_rowid() is a correctness
    /// bug, not merely a slow path: the gate is released between the two, last_insert_rowid() is a
    /// property of the connection, and there is exactly one connection shared by every thread — so
    /// a concurrent insert on another thread hands back its rowid instead. RETURNING keeps the
    /// insert and the id atomic without needing a coarser lock or an explicit transaction.
    /// </summary>
    public long InsertReturningId(string insertSql, params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Cmd(insertSql + " RETURNING id;", args);
            return Convert.ToInt64(cmd.ExecuteScalar()!);
        }
    }

    /// <summary>Runs a reader under the gate, materialising rows before releasing it.</summary>
    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map,
                            params (string Name, object? Value)[] args)
    {
        lock (_gate)
        {
            using var cmd = Cmd(sql, args);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    /// <summary>
    /// Closes the connection under the gate, so a statement still running on the scan thread
    /// finishes first. Without that, closing the window mid-scan frees the sqlite3 handle out
    /// from under a thread that is still stepping a statement.
    /// </summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _connection.Dispose();
        }
    }

    // ---------------------------------------------------------------- time

    public static string Utc(DateTime t) => t.ToUniversalTime().ToString("o");
    public static DateTime ParseUtc(string s) => DateTime.Parse(s, null,
        System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
    public static DateTime? ParseUtcOrNull(object? s) =>
        s is string str && str.Length > 0 ? ParseUtc(str) : null;
}
