using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace DazeControl.Core.Updates;

/// <summary>The updater the editor's Updates card and <c>dctl update</c> drive; Velopack for real, a fake in tests.</summary>
public interface IUpdateClient
{
    /// <summary>False outside a Velopack install (a Debug build or a published folder): nothing to update.</summary>
    bool IsInstalled { get; }

    /// <summary>The version this copy is, when installed.</summary>
    string? CurrentVersion { get; }

    /// <summary>A downloaded update newer than this copy, if any; it applies the next time the engine starts.</summary>
    string? PendingVersion { get; }

    /// <summary>The newest release if it's newer than this copy, else null. Remembers it for <see cref="DownloadAsync"/>.</summary>
    Task<string?> FindAsync(CancellationToken cancellationToken);

    Task DownloadAsync(CancellationToken cancellationToken);

    /// <summary>Starts the updater, which waits for this process to exit, applies the update and starts the engine.</summary>
    void ApplyAfterExit();
}

/// <summary>
/// Velopack against this repo's GitHub Releases (D-015, OSD D-029/D-030): stable releases only, no token. The engine,
/// the editor and <c>dctl</c> all sit in the install's <c>current</c> folder, so each finds the same install.
/// </summary>
public sealed class VelopackUpdateClient : IUpdateClient
{
    public const string RepoUrl = "https://github.com/dazewell/daze-control";

    /// <summary>A local feed folder (vpk pack output) in place of GitHub, for testing updates (build skill).</summary>
    public const string SourceOverrideVariable = "DAZECONTROL_UPDATE_SOURCE";

    private readonly UpdateManager _manager = CreateManager();
    private UpdateInfo? _found;

    public bool IsInstalled => _manager.IsInstalled;

    public string? CurrentVersion => _manager.IsInstalled ? _manager.CurrentVersion?.ToString() : null;

    public string? PendingVersion => _manager.UpdatePendingRestart?.Version.ToString();

    public async Task<string?> FindAsync(CancellationToken cancellationToken)
    {
        _found = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        var version = _found?.TargetFullRelease.Version.ToString();
        // Already downloaded and waiting for a restart: nothing new (the daily check mustn't fetch it again).
        if (version is not null && version == PendingVersion)
            _found = null;
        return _found is null ? null : version;
    }

    public Task DownloadAsync(CancellationToken cancellationToken) =>
        _found is null
            ? throw new InvalidOperationException("Nothing found to download.")
            : _manager.DownloadUpdatesAsync(_found, progress: null, cancellationToken);

    // Started from the editor, which doesn't know the engine's args: the updater starts the engine on the default config.
    public void ApplyAfterExit() =>
        _manager.WaitExitThenApplyUpdates(_manager.UpdatePendingRestart, silent: true, restart: true, restartArgs: []);

    /// <summary>
    /// At engine start: hands a downloaded update to the updater and returns true, and the caller exits; the updater
    /// applies it and starts the engine again. Skipped right after such a restart, so a failed apply can't loop.
    /// </summary>
    /// <param name="args">This start's arguments (a config path), handed to the restarted engine.</param>
    public static bool ApplyPendingAtStart(bool restartedByUpdater, string[] args)
    {
        if (restartedByUpdater)
            return false;
        var manager = CreateManager();
        if (!manager.IsInstalled || manager.UpdatePendingRestart is not { } pending)
            return false;
        manager.WaitExitThenApplyUpdates(pending, silent: true, restart: true, restartArgs: args);
        return true;
    }

    /// <summary>True when this process runs from a Velopack install.</summary>
    public static bool IsInstalledCopy() => CreateManager().IsInstalled;

    /// <summary>Velopack sets it when its updater starts the app; read it before <c>VelopackApp.Run()</c>, which clears it.</summary>
    public static bool RestartedByUpdater() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VELOPACK_RESTART"));

    private static UpdateManager CreateManager()
    {
        // UpdateManager needs the locator VelopackApp.Run() sets; the engine ran it for its hooks, the editor and dctl
        // haven't. Not the main exe, so no hook ever reaches them; no auto-apply (only the engine applies, D-043).
        if (!VelopackLocator.IsCurrentSet)
            VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        return Environment.GetEnvironmentVariable(SourceOverrideVariable) is { Length: > 0 } feed
            ? new UpdateManager(feed)
            : new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
    }
}
