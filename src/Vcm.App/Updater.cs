using Velopack;
using Velopack.Sources;

namespace Vcm.App;

/// <summary>
/// Self-update against the project's GitHub releases.
///
/// The release repository is public, so no credentials are needed or embedded. That is a
/// deliberate simplification over the private-repo approach, which would require baking a
/// read-only token into the shipped binary — where anyone holding the binary could extract it,
/// and where a silent 404 on every check is the failure mode when it is missing.
///
/// Portable deployments update themselves exactly like installed ones: Velopack's notion of
/// "installed" is "running from a Velopack layout", which the portable zip also is.
/// </summary>
public sealed class Updater
{
    public const string RepoUrl = "https://github.com/mouseling/video-collection";

    private readonly UpdateManager _manager;
    private UpdateInfo? _pending;

    private Updater(UpdateManager manager) => _manager = manager;

    /// <summary>The version waiting to be installed, if a check has found one.</summary>
    public string? PendingVersion => _pending?.TargetFullRelease.Version.ToString();

    public bool HasPendingUpdate => _pending is not null;

    /// <summary>
    /// Returns null when updates are not possible — a dev run, or a plain folder publish rather
    /// than a Velopack deployment. Callers should treat that as "no updater" and carry on.
    /// </summary>
    public static Updater? TryCreate()
    {
        try
        {
            var source = new GithubSource(repoUrl: RepoUrl, accessToken: null, prerelease: false);

            var manager = new UpdateManager(source);
            return manager.IsInstalled ? new Updater(manager) : null;
        }
        catch
        {
            // Not deployed by Velopack. Normal during development.
            return null;
        }
    }

    /// <summary>
    /// Looks for a newer release. Failures are swallowed and reported as "no update" — being
    /// offline is ordinary and must never interrupt playback with an error.
    /// </summary>
    public async Task<string?> CheckAsync()
    {
        try
        {
            _pending = await _manager.CheckForUpdatesAsync();
            return PendingVersion;
        }
        catch
        {
            _pending = null;
            return null;
        }
    }

    /// <summary>
    /// Downloads the pending update and restarts into it. Returns false if the download fails;
    /// on success the process is replaced and nothing after this runs.
    /// </summary>
    public async Task<bool> ApplyAsync(Action? beforeRestart = null)
    {
        if (_pending is null) return false;

        try
        {
            await _manager.DownloadUpdatesAsync(_pending);
        }
        catch
        {
            return false;
        }

        // Last chance to close the database and persist the open viewing session; the call below
        // terminates the process immediately.
        beforeRestart?.Invoke();
        _manager.ApplyUpdatesAndRestart(_pending);
        return true;
    }
}
