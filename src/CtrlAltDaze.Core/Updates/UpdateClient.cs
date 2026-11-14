using Velopack;
using Velopack.Locators;
using Velopack.Sources;

namespace CtrlAltDaze.Core.Updates;

/// <summary>The updater the editor's Updates card and <c>cad update</c> drive; Velopack for real, a fake in tests.</summary>
public interface IUpdateClient
{
    /// <summary>False outside a Velopack install (a Debug build or a published folder): nothing to update.</summary>
    bool IsInstalled { get; }

    /// <summary>The version this copy is, when installed.</summary>
    string? CurrentVersion { get; }

    /// <summary>Which releases <see cref="FindAsync"/> looks at (D-108). Set it before checking.</summary>
    UpdateChannel Channel { get; set; }

    /// <summary>A downloaded update newer than this copy, if any; it applies the next time the engine starts.</summary>
    string? PendingVersion { get; }

    /// <summary>The newest release if it's newer than this copy, else null. Remembers it for <see cref="DownloadAsync"/>.</summary>
    Task<string?> FindAsync(CancellationToken cancellationToken);

    /// <summary>Downloads what <see cref="FindAsync"/> found; <paramref name="progress"/> gets 0 to 100.</summary>
    Task DownloadAsync(Action<int>? progress, CancellationToken cancellationToken);

    /// <summary>Starts the updater, which waits for this process to exit, applies the update and starts the engine.</summary>
    void ApplyAfterExit();
}

/// <summary>
/// Velopack against this repo's GitHub Releases (D-015, OSD D-029/D-030), no token: stable releases, or also release
/// candidates on <see cref="UpdateChannel.ReleaseCandidates"/> (D-108). The engine,
/// the editor and <c>cad</c> all sit in the install's <c>current</c> folder, so each finds the same install.
/// </summary>
public sealed class VelopackUpdateClient : IUpdateClient
{
    public const string RepoUrl = "https://github.com/dazewell/ctrl-alt-daze";

    /// <summary>A local feed folder (vpk pack output) in place of GitHub, for testing updates (build skill).</summary>
    public const string SourceOverrideVariable = "CTRLALTDAZE_UPDATE_SOURCE";

    private readonly Func<UpdateChannel, UpdateManager> _factory;
    private UpdateManager _manager;
    private UpdateChannel _channel;
    private UpdateInfo? _found;

    public VelopackUpdateClient(UpdateChannel channel = UpdateChannel.Stable) : this(channel, CreateManager)
    {
    }

    internal VelopackUpdateClient(UpdateChannel channel, Func<UpdateChannel, UpdateManager> factory)
    {
        _factory = factory;
        _channel = channel;
        _manager = factory(channel);
    }

    public UpdateChannel Channel
    {
        get => _channel;
        set
        {
            if (value == _channel)
                return;
            _manager = _factory(value);
            _channel = value;
            _found = null;
            _foundBy = null;
        }
    }

    public bool IsInstalled => _manager.IsInstalled;

    public string? CurrentVersion => _manager.IsInstalled ? _manager.CurrentVersion?.ToString() : null;

    public string? PendingVersion => _manager.UpdatePendingRestart?.Version.ToString();

    // The manager that found the update downloads it: a channel switch mid-check must not mix an RC found by the old
    // manager with the new one (and a result that arrives after the switch is dropped).
    private UpdateManager? _foundBy;

    public async Task<string?> FindAsync(CancellationToken cancellationToken)
    {
        var manager = _manager;
        var found = await manager.CheckForUpdatesAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!ReferenceEquals(manager, _manager))
            return null;
        var version = found?.TargetFullRelease.Version.ToString();
        // Already downloaded and waiting for a restart: nothing new unless strictly newer (the daily check mustn't fetch it
        // again, and a feed that regressed or a channel switch must not replace the waiting update with an older one).
        if (found is not null && _manager.UpdatePendingRestart is { } waiting && found.TargetFullRelease.Version <= waiting.Version)
            found = null;
        _found = found;
        _foundBy = found is null ? null : manager;
        return _found is null ? null : version;
    }

    public Task DownloadAsync(Action<int>? progress, CancellationToken cancellationToken) =>
        _found is null || _foundBy is null
            ? throw new InvalidOperationException("Nothing found to download.")
            : _foundBy.DownloadUpdatesAsync(_found, progress, cancellationToken);

    /// <summary>At engine start: looks again and downloads a release newer than the downloaded one; any failure keeps it.</summary>
    public async Task<string?> RefreshBeforeApplyAsync(CancellationToken cancellationToken)
    {
        try
        {
            // FindAsync skips the version already waiting, so a result means something newer than the download.
            if (await FindAsync(cancellationToken).ConfigureAwait(false) is not null)
                await DownloadAsync(null, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Offline, rate limit, timeout: the version already downloaded still installs.
        }
        return PendingVersion;
    }

    // Started from the editor, which doesn't know the engine's args: the updater starts the engine on the default config.
    public void ApplyAfterExit() =>
        _manager.WaitExitThenApplyUpdates(_manager.UpdatePendingRestart, silent: true, restart: true, restartArgs: []);

    /// <summary>
    /// At engine start: hands a downloaded update to the updater and returns true, and the caller exits; the updater
    /// applies it and starts the engine again. Skipped right after such a restart, so a failed apply can't loop.
    /// </summary>
    /// <param name="args">This start's arguments (a config path), handed to the restarted engine.</param>
    /// <param name="channelFile">The channel file next to a custom config (as the editor uses); null = the per-user default.</param>
    public static bool ApplyPendingAtStart(bool restartedByUpdater, string[] args, string? channelFile = null)
    {
        if (restartedByUpdater)
            return false;
        var manager = CreateManager(UpdateChannel.Stable);
        if (!manager.IsInstalled || manager.UpdatePendingRestart is null)
            return false;
        // A newer release may be out since the download (D-109): look once more, briefly, and fetch it. Any failure
        // (offline, slow) keeps the downloaded one.
        var client = new VelopackUpdateClient(new UpdateChannelStore(channelFile ?? UpdateChannelStore.DefaultPath).Load());
        using (var limit = new CancellationTokenSource(RefreshAtStartLimit))
        {
            try { client.RefreshBeforeApplyAsync(limit.Token).GetAwaiter().GetResult(); }
            catch (Exception e) when (e is not OutOfMemoryException) { }
        }
        var target = client._manager.UpdatePendingRestart ?? manager.UpdatePendingRestart;
        client._manager.WaitExitThenApplyUpdates(target, silent: true, restart: true, restartArgs: args);
        return true;
    }

    private static readonly TimeSpan RefreshAtStartLimit = TimeSpan.FromSeconds(30);

    /// <summary>True when version <paramref name="candidate"/> is strictly newer than <paramref name="than"/> (semver, so an RC is older than its release).</summary>
    public static bool IsNewer(string candidate, string than) =>
        SemanticVersion.TryParse(candidate, out var a) && SemanticVersion.TryParse(than, out var b) && a > b;

    /// <summary>True when this process runs from a Velopack install.</summary>
    public static bool IsInstalledCopy() => CreateManager(UpdateChannel.Stable).IsInstalled;

    /// <summary>Velopack sets it when its updater starts the app; read it before <c>VelopackApp.Run()</c>, which clears it.</summary>
    public static bool RestartedByUpdater() =>
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VELOPACK_RESTART"));

    private static UpdateManager CreateManager(UpdateChannel channel)
    {
        // UpdateManager needs the locator VelopackApp.Run() sets; the engine ran it for its hooks, the editor and cad
        // haven't. Not the main exe, so no hook ever reaches them; no auto-apply (only the engine applies, D-043).
        if (!VelopackLocator.IsCurrentSet)
            VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        return Environment.GetEnvironmentVariable(SourceOverrideVariable) is { Length: > 0 } feed
            ? new UpdateManager(feed)
            : new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: channel.IncludesPreReleases()));
    }
}
