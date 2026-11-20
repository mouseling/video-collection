using Vcm.Core;

namespace Vcm.Core.Tests;

/// <summary>
/// A throwaway library on disk: a temp directory holding the database and any fake video files a
/// test needs.
///
/// A real file rather than ":memory:" because <see cref="Database"/> resolves the path on the way
/// in, and because a file exercises WAL exactly as shipped. Tests make their own byte-pattern
/// files — testdata/ is gitignored and fetched by a 280 MB script, so it can never be a fixture.
/// </summary>
public sealed class TempLibrary : IDisposable
{
    public string Root { get; }
    public Database Db { get; }
    public Library Library { get; }

    public TempLibrary()
    {
        Root = Path.Combine(Path.GetTempPath(), "vcm-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);

        Db = new Database(Path.Combine(Root, "library.db"));
        Library = new Library(Db);
    }

    /// <summary>Creates a deterministic fake video. Only the extension matters to the scanner.</summary>
    public string MakeVideo(string relativePath, int sizeBytes = 2048, byte seed = 1)
    {
        var full = Path.Combine(Root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        var bytes = new byte[sizeBytes];
        for (var i = 0; i < bytes.Length; i++) bytes[i] = (byte)(seed + i % 251);
        File.WriteAllBytes(full, bytes);
        return full;
    }

    public string AddRoot(string relativePath = "")
    {
        var path = string.IsNullOrEmpty(relativePath) ? Root : Path.Combine(Root, relativePath);
        Directory.CreateDirectory(path);

        var root = Library.AddRoot(path, out var error);
        if (root is null) throw new InvalidOperationException($"AddRoot failed: {error}");
        return path;
    }

    public ScanResult Scan(CancellationToken ct = default) => new Scanner(Db).Scan(null, ct);

    public long Count(string table) =>
        Convert.ToInt64(Db.Scalar($"SELECT COUNT(*) FROM {table}") ?? 0L);

    public void Dispose()
    {
        Db.Dispose();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { /* best effort */ }
    }
}
