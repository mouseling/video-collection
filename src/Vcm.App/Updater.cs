using Vcm.Core;
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
    /// Why the last check or download failed, for the status line. Kept because an update that
    /// never arrives is otherwise indistinguishable from there being no update — the exact
    /// confusion that a silent permanent 404 would cause.
    /// </summary>
    public string? LastError { get; private set; }

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
        catch (Exception ex)
        {
            // Not deployed by Velopack. Normal during development.
            Log.Write("updater unavailable (expected outside a Velopack deployment)", ex);
            return null;
        }
    }

    /// <summary>
    /// Looks for a newer release. Failures are reported as "no update" rather than raised — being
    /// offline is ordinary and must never interrupt playback — but the reason is kept in
    /// <see cref="LastError"/> and logged.
    /// </summary>
    public async Task<string?> CheckAsync()
    {
        try
        {
            _pending = await _manager.CheckForUpdatesAsync();
            LastError = null;
            return PendingVersion;
        }
        catch (Exception ex)
        {
            _pending = null;
            LastError = ex.Message;
            Log.Write("update check failed (usually no internet)", ex);
            return null;
        }
    }

    /// <summary>
    /// Downloads the pending update and restarts into it. Returns false only if the download
    /// failed, in which case the application is untouched and can carry on.
    /// </summary>
    public async Task<bool> ApplyAsync(Action? beforeRestart = null)
    {
        if (_pending is null) return false;

        try
        {
            await _manager.DownloadUpdatesAsync(_pending);
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            Log.Write("update download failed", ex);
            return false;
        }

        // Past this point there is no way back: beforeRestart closes the database and ends the
        // open viewing session.
        beforeRestart?.Invoke();

        try
        {
            _manager.ApplyUpdatesAndRestart(_pending);
        }
        catch (Exception ex)
        {
            Log.Write("applying the update failed after shutdown", ex);
        }

        // ApplyUpdatesAndRestart replaces the process and should never return. If it does, the
        // database is already closed and the player already stopped, so returning to the UI would
        // leave a window that looks alive and throws on every click. Exiting is the honest end.
        Environment.Exit(0);
        return true;
    }
}
