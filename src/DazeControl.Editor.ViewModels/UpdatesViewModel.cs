using CommunityToolkit.Mvvm.ComponentModel;
using DazeControl.Core;
using DazeControl.Core.Updates;

namespace DazeControl.Editor.ViewModels;

/// <summary>
/// Settings › Updates (D-043): what the installed copy knows, "Check for updates" (finds and downloads a newer release)
/// and "Restart now" once one is downloaded. The engine checks daily on its own; this card is for doing it now.
/// </summary>
public sealed partial class UpdatesViewModel : ObservableObject
{
    private readonly IUpdateClient? _client;
    private readonly Action<Action> _post;
    private bool _checking;

    /// <param name="client">Null outside an install (Debug, a published folder).</param>
    public UpdatesViewModel(IUpdateClient? client, Action<Action> post)
    {
        _client = client is { IsInstalled: true } ? client : null;
        _post = post;
        Show(_client?.PendingVersion is { } pending ? Ready(pending) : Idle());
    }

    /// <summary>Raised after Restart now handed the update to the updater; the editor then closes.</summary>
    public event Action? RestartRequested;

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

    /// <summary>The updater waits for the editor to exit, closes the engine cleanly, applies, starts the engine.</summary>
    public void Restart()
    {
        if (_client is null || !CanRestart)
            return;
        _client.ApplyAfterExit();
        RestartRequested?.Invoke();
    }

    private (string, bool, bool) Idle() => _client is null
        ? ("This copy isn't installed, so it doesn't update.", false, false)
        : ($"Version {_client.CurrentVersion}. {AppInfo.ProductName} checks once a day.", true, false);

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
