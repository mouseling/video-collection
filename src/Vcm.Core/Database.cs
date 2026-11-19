using Microsoft.Data.Sqlite;

namespace Vcm.Core;

/// <summary>
/// Owns the SQLite connection and the schema. One connection, guarded by <see cref="Gate"/>,
/// because scanning runs on a background thread while the UI reads on its own.
/// </summary>
public sealed class Database : IDisposable
{
    public const int SchemaVersion = 1;

    public SqliteConnection Connection { get; }
    public object Gate { get; } = new();
    public string Path { get; }

    public Database(string path)
    {
        Path = path;
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        Connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            ForeignKeys = true,
        }.ToString());
        Connection.Open();

        Exec("PRAGMA journal_mode=WAL;");
        Exec("PRAGMA synchronous=NORMAL;");
        Migrate();
    }

    /// <summary>Opens the library stored in the user's roaming profile.</summary>
    public static Database OpenDefault()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VideoCollectionManager");
        return new Database(System.IO.Path.Combine(dir, "library.db"));
    }

    private void Migrate()
    {
        var current = Convert.ToInt32(Scalar("PRAGMA user_version;") ?? 0);
        if (current >= SchemaVersion) return;

        if (current < 1)
        {
            Exec(Schema);
            Exec($"PRAGMA user_version={SchemaVersion};");
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

    public SqliteCommand Cmd(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = Connection.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd;
    }

    public int Exec(string sql, params (string Name, object? Value)[] args)
    {
        lock (Gate)
        {
            using var cmd = Cmd(sql, args);
            return cmd.ExecuteNonQuery();
        }
    }

    public object? Scalar(string sql, params (string Name, object? Value)[] args)
    {
        lock (Gate)
        {
            using var cmd = Cmd(sql, args);
            var v = cmd.ExecuteScalar();
            return v == DBNull.Value ? null : v;
        }
    }

    /// <summary>Runs a reader under the gate, materialising rows before releasing it.</summary>
    public List<T> Query<T>(string sql, Func<SqliteDataReader, T> map,
                            params (string Name, object? Value)[] args)
    {
        lock (Gate)
        {
            using var cmd = Cmd(sql, args);
            using var r = cmd.ExecuteReader();
            var list = new List<T>();
            while (r.Read()) list.Add(map(r));
            return list;
        }
    }

    public void Dispose()
    {
        Connection.Dispose();
    }

    // ---------------------------------------------------------------- time

    public static string Utc(DateTime t) => t.ToUniversalTime().ToString("o");
    public static DateTime ParseUtc(string s) => DateTime.Parse(s, null,
        System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
    public static DateTime? ParseUtcOrNull(object? s) =>
        s is string str && str.Length > 0 ? ParseUtc(str) : null;
}
