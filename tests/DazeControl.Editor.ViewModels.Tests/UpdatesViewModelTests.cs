using DazeControl.Core.Updates;
using DazeControl.Editor.ViewModels;

namespace DazeControl.Editor.ViewModels.Tests;

public sealed class UpdatesViewModelTests
{
    private sealed class FakeUpdates : IUpdateClient
    {
        public bool IsInstalled { get; init; } = true;
        public string? CurrentVersion => "0.17.0";
        public string? PendingVersion { get; set; }
        public string? Newer { get; set; }
        public Exception? FindFails { get; set; }
        public bool Applied { get; private set; }

        public Task<string?> FindAsync(CancellationToken cancellationToken) =>
            FindFails is { } e ? Task.FromException<string?>(e) : Task.FromResult(Newer);

        public Task DownloadAsync(CancellationToken cancellationToken)
        {
            PendingVersion = Newer;
            return Task.CompletedTask;
        }

        public void ApplyAfterExit() => Applied = true;
    }

    [Fact]
    public void NotInstalledCanNeitherCheckNorRestart()
    {
        var updates = new UpdatesViewModel(new FakeUpdates { IsInstalled = false }, a => a());
        Assert.False(updates.CanCheck);
        Assert.False(updates.CanRestart);
        Assert.Contains("isn't installed", updates.Status, StringComparison.Ordinal);
        Assert.False(new UpdatesViewModel(null, a => a()).CanCheck);
    }

    [Fact]
    public void AnUpdateDownloadedEarlierIsReadyAtOnce()
    {
        var updates = new UpdatesViewModel(new FakeUpdates { PendingVersion = "0.18.0" }, a => a());
        Assert.True(updates.CanRestart);
        Assert.Contains("0.18.0", updates.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckSaysUpToDateWhenNothingIsNewer()
    {
        var updates = new UpdatesViewModel(new FakeUpdates(), a => a());
        await updates.CheckAsync();
        Assert.Equal("Up to date (0.17.0).", updates.Status);
        Assert.True(updates.CanCheck);
        Assert.False(updates.CanRestart);
    }

    [Fact]
    public async Task CheckDownloadsANewerReleaseAndRestartHandsItToTheUpdater()
    {
        var client = new FakeUpdates { Newer = "0.18.0" };
        var updates = new UpdatesViewModel(client, a => a());
        var restarted = false;
        updates.RestartRequested += () => restarted = true;

        await updates.CheckAsync();
        Assert.True(updates.CanRestart);
        Assert.False(updates.CanCheck);
        updates.Restart();

        Assert.True(client.Applied);
        Assert.True(restarted);
    }

    [Fact]
    public async Task AFailedCheckSaysWhyAndKeepsADownloadedUpdate()
    {
        var updates = new UpdatesViewModel(new FakeUpdates { FindFails = new HttpRequestException("offline") }, a => a());
        await updates.CheckAsync();
        Assert.Equal("Couldn't check: offline", updates.Status);
        Assert.True(updates.CanCheck);

        var ready = new UpdatesViewModel(new FakeUpdates { PendingVersion = "0.18.0", FindFails = new HttpRequestException("offline") }, a => a());
        await ready.CheckAsync();
        Assert.True(ready.CanRestart);
    }

    [Fact]
    public void RestartDoesNothingWithoutAReadyUpdate()
    {
        var client = new FakeUpdates();
        new UpdatesViewModel(client, a => a()).Restart();
        Assert.False(client.Applied);
    }
}
