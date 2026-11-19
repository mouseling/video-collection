namespace Vcm.App;

/// <summary>
/// Decides where the library database lives.
///
/// Velopack lays a deployed app out as <c>&lt;root&gt;/current/VideoCollectionManager.exe</c> with
/// <c>Update.exe</c> in the root, and every update replaces <c>current/</c> wholesale. Anything
/// written beside the executable is therefore destroyed on the next update, so the database goes
/// in the root instead: it survives updates, and because it sits inside the unzipped folder the
/// whole installation stays portable — copy the folder to a stick and the history goes with it.
///
/// Outside a Velopack layout (a dev run, or a plain folder publish) there is no such root, so the
/// usual per-user application data directory is used.
/// </summary>
public static class AppPaths
{
    public const string DbFileName = "library.db";

    private static string? _cached;

    public static bool IsPortableDeployment => VelopackRoot is not null;

    /// <summary>The persistent Velopack app root, or null when not running from one.</summary>
    public static string? VelopackRoot
    {
        get
        {
            var baseDir = AppContext.BaseDirectory
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            var parent = Directory.GetParent(baseDir)?.FullName;
            if (string.IsNullOrEmpty(parent)) return null;

            // Update.exe beside current/ is what identifies a Velopack deployment.
            return File.Exists(Path.Combine(parent, "Update.exe")) ? parent : null;
        }
    }

    public static string LibraryDbPath => _cached ??= Resolve();

    private static string Resolve()
    {
        if (VelopackRoot is { } root) return Path.Combine(root, DbFileName);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "VideoCollectionManager",
            DbFileName);
    }
}
