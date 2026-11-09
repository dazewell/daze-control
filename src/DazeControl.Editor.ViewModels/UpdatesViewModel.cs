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
        Show(_client?.PendingVersion is { } pending ? Ready(pending) : Idle());
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

    [ObservableProperty]
    public partial bool CanRestart { get; private set; }

    public async Task CheckAsync()
    {
        if (_client is null || _checking)
            return;
        _checking = true;
        Show(("Checking for updates…", false, false));
        try
        {
            var found = await _client.FindAsync(CancellationToken.None).ConfigureAwait(false);
            if (found is null)
            {
                Post(_client.PendingVersion is { } pending ? Ready(pending) : ($"Up to date ({_client.CurrentVersion}).", true, false));
                return;
            }
            Post(($"Downloading version {found}…", false, false));
            await _client.DownloadAsync(CancellationToken.None).ConfigureAwait(false);
            Post(Ready(found));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Network, GitHub's rate limit, disk: the engine's daily check tries again too.
            Post(_client.PendingVersion is { } pending ? Ready(pending) : ($"Couldn't check: {e.Message}", true, false));
        }
        finally
        {
            _checking = false;
        }
    }

    /// <summary>
    /// Looks once more for a newer release (the one downloaded may be old by now), then hands the newest to the updater,
    /// which waits for the editor to exit, closes the engine cleanly, applies and starts the engine. True when it did:
    /// the caller then closes the editor. False (with the reason in Status) otherwise.
    /// </summary>
    public async Task<bool> RestartAsync()
    {
        if (_client is null || !CanRestart || _checking)
            return false;
        _checking = true;
        Show(("Looking for a newer version…", false, false));
        try
        {
            await _client.RefreshBeforeApplyAsync(CancellationToken.None).ConfigureAwait(false);
            _client.ApplyAfterExit();
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Update.exe missing, quarantined or blocked: stay open, the update still applies at the next engine start.
            Post(($"Couldn't start the update: {e.Message} It installs the next time {AppInfo.ProductName} starts.", true, true));
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
        ($"Version {version} is ready. It installs the next time {AppInfo.ProductName} starts, or now.", false, true);

    private void Post((string Status, bool CanCheck, bool CanRestart) state) => _post(() => Show(state));

    private void Show((string Status, bool CanCheck, bool CanRestart) state)
    {
        Status = state.Status;
        CanCheck = state.CanCheck;
        CanRestart = state.CanRestart;
    }
}
