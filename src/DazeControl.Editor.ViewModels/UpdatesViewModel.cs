using CommunityToolkit.Mvvm.ComponentModel;
using DazeControl.Core;
using DazeControl.Core.Updates;

namespace DazeControl.Editor.ViewModels;

/// <summary>
/// About › Updates (D-043): what the installed copy knows, "Check for updates" (finds and downloads a newer release)
/// and "Restart now" once one is downloaded. The engine checks daily on its own; this card is for doing it now.
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly IUpdateClient? _client;
    private readonly Action<Action> _post;
    private bool _checking;

    private readonly UpdateChannelStore? _store;

    /// <param name="client">Null outside an install (Debug, a published folder).</param>
    /// <param name="store">The per-user channel file (D-108); null keeps the choice in memory only (tests).</param>
    public UpdatesViewModel(IUpdateClient? client, Action<Action> post, UpdateChannelStore? store = null)
    {
        _client = client is { IsInstalled: true } ? client : null;
        _post = post;
        _store = store;
        if (store is not null)
            _channelIndex = (int)store.Load();
        if (_client is not null)
            _client.Channel = (UpdateChannel)_channelIndex;
        _offered = _client?.PendingVersion;
        Show(_offered is { } pending ? Ready(pending) : Idle());
    }

    public string ChannelNote { get; } = "Release candidates come first and may have rough edges. Going back to Stable keeps the version you have until a newer stable one is out.";

    private int _channelIndex;

    /// <summary>0 = Stable, 1 = Release candidates (<see cref="UpdateChannel"/>'s order). Saved at once; the next check uses it.</summary>
    public int ChannelIndex
    {
        get => _channelIndex;
        set
        {
            if (value is < 0 or > 1 || value == _channelIndex)
                return;
            var channel = (UpdateChannel)value;
            try
            {
                _store?.Save(channel);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The old choice stays; the combo box is put back by the property change below.
                OnPropertyChanged();
                Show(($"Couldn't save the update channel: {e.Message}", _client is not null, false));
                return;
            }
            _channelIndex = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ChannelLabel));
            if (_client is null)
                return;
            _client.Channel = channel;
            // A download already waiting still installs; otherwise the old "up to date" no longer answers for the new channel.
            if (!CanRestart && !_checking)
                Show(Idle());
        }
    }

    /// <summary>The channel's name in the card's status line.</summary>
    public string ChannelLabel => ((UpdateChannel)_channelIndex).Label();

    [ObservableProperty]
    public partial string Status { get; private set; } = "";

    [ObservableProperty]
    public partial bool CanCheck { get; private set; }

    /// <summary>True when Install is offered: a release was found, or one is already downloaded.</summary>
    [ObservableProperty]
    public partial bool CanRestart { get; private set; }

    // The version the card last showed as installable; Install compares what it finds at click time against it.
    private string? _offered;

    /// <summary>Looks only: names the newest release, downloads nothing. Safe to press any time.</summary>
    public async Task CheckAsync()
    {
        if (_client is null || _checking)
            return;
        _checking = true;
        Show(("Checking for updates…", false, false));
        try
        {
            var found = await _client.FindAsync(CancellationToken.None).ConfigureAwait(false);
            Post(StateFor(found));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Network, GitHub's rate limit: the engine's daily check tries again too.
            Post(_client.PendingVersion is { } pending ? Ready(pending) : ($"Couldn't check: {e.Message}", true, false));
        }
        finally
        {
            _checking = false;
        }
    }

    private (string, bool, bool) StateFor(string? found)
    {
        if (found is not null)
        {
            _offered = found;
            return ($"Version {found} is available. Install downloads it and restarts {AppInfo.ProductName}.", true, true);
        }
        if (_client?.PendingVersion is { } pending)
        {
            _offered = pending;
            return Ready(pending);
        }
        _offered = null;
        return ($"Up to date ({_client?.CurrentVersion}).", true, false);
    }

    /// <summary>
    /// Install: looks for the newest release right now, downloads it (progress in Status) and hands it to the updater, which
    /// waits for the editor to exit, closes the engine, applies and starts it again. True when it did: the caller closes the
    /// editor. If a newer release than the one shown has appeared, it stops first and says so, so the click that follows
    /// installs the newer one knowingly. False (reason in Status) otherwise.
    /// </summary>
    public async Task<bool> RestartAsync()
    {
        if (_client is null || !CanRestart || _checking)
            return false;
        _checking = true;
        var shown = _offered;
        Show(("Looking for the newest version…", false, false));
        try
        {
            string? found = null;
            try
            {
                found = await _client.FindAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // Offline: a download that already finished still installs; otherwise say why and stay put.
                if (_client.PendingVersion is null)
                {
                    Post(($"Couldn't check: {e.Message}", true, shown is not null));
                    return false;
                }
            }
            if (found is not null && shown is not null && found != shown)
            {
                _offered = found;
                Post(($"Version {found} is out, newer than version {shown}. Install now installs version {found}, not {shown}.", true, true));
                return false;
            }
            if (found is not null)
            {
                Post(($"Downloading version {found}…", false, false));
                await _client.DownloadAsync(percent => Post(($"Downloading version {found}… {percent} %", false, false)), CancellationToken.None).ConfigureAwait(false);
            }
            _client.ApplyAfterExit();
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Download failed, or Update.exe is missing or blocked: stay open; a downloaded update still applies at the next engine start.
            Post(($"Couldn't install: {e.Message}", true, true));
            return false;
        }
        finally
        {
            _checking = false;
        }
    }

    private (string, bool, bool) Idle() => _client is null
        ? ("This copy isn't installed, so it doesn't update.", false, false)
        : ($"Version {_client.CurrentVersion}, {ChannelLabel}. {AppInfo.ProductName} checks once a day.", true, false);

    private static (string, bool, bool) Ready(string version) =>
        ($"Version {version} is downloaded. Install restarts {AppInfo.ProductName} into it; it also installs by itself the next time {AppInfo.ProductName} starts.", false, true);

    private void Post((string Status, bool CanCheck, bool CanRestart) state) => _post(() => Show(state));

    private void Show((string Status, bool CanCheck, bool CanRestart) state)
    {
        Status = state.Status;
        CanCheck = state.CanCheck;
        CanRestart = state.CanRestart;
    }
}
