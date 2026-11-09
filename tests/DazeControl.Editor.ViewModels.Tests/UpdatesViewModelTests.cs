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
        public UpdateChannel Channel { get; set; }
        public bool Applied { get; private set; }
        public Exception? ApplyFails { get; init; }

        public Task<string?> FindAsync(CancellationToken cancellationToken) =>
            FindFails is { } e ? Task.FromException<string?>(e) : Task.FromResult(Newer == PendingVersion ? null : Newer);

        public Task DownloadAsync(Action<int>? progress, CancellationToken cancellationToken)
        {
            progress?.Invoke(50);
            PendingVersion = Newer;
            return Task.CompletedTask;
        }

        public void ApplyAfterExit() => Applied = ApplyFails is null ? true : throw ApplyFails;
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
    public async Task CheckOnlyNamesTheReleaseAndInstallDownloadsAndRestarts()
    {
        var client = new FakeUpdates { Newer = "0.18.0" };
        var updates = new UpdatesViewModel(client, a => a());

        await updates.CheckAsync();
        Assert.Contains("0.18.0 is available", updates.Status, StringComparison.Ordinal);
        Assert.Null(client.PendingVersion);
        Assert.True(updates.CanCheck);
        Assert.True(updates.CanRestart);

        Assert.True(await updates.RestartAsync());
        Assert.Equal("0.18.0", client.PendingVersion);
        Assert.True(client.Applied);
    }

    [Fact]
    public async Task InstallThatFindsANewerReleaseThanShownSaysSoAndInstallsItOnTheNextClick()
    {
        var client = new FakeUpdates { Newer = "0.18.0" };
        var updates = new UpdatesViewModel(client, a => a());
        await updates.CheckAsync();

        client.Newer = "0.19.0";
        Assert.False(await updates.RestartAsync());
        Assert.Contains("0.19.0 is out, newer than version 0.18.0", updates.Status, StringComparison.Ordinal);
        Assert.Contains("installs version 0.19.0, not 0.18.0", updates.Status, StringComparison.Ordinal);
        Assert.False(client.Applied);
        Assert.True(updates.CanRestart);

        Assert.True(await updates.RestartAsync());
        Assert.Equal("0.19.0", client.PendingVersion);
        Assert.True(client.Applied);
    }

    [Fact]
    public async Task InstallWhenTheOfferedReleaseIsGoneSaysUpToDateAndAppliesNothing()
    {
        var client = new FakeUpdates { Newer = "0.18.0" };
        var updates = new UpdatesViewModel(client, a => a());
        await updates.CheckAsync();

        client.Newer = null;
        Assert.False(await updates.RestartAsync());
        Assert.False(client.Applied);
        Assert.False(updates.CanRestart);
        Assert.StartsWith("Up to date", updates.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstallDoesNotTreatAnOlderReleaseAsNewerThanShown()
    {
        var client = new FakeUpdates { Newer = "0.19.0-rc.1" };
        var updates = new UpdatesViewModel(client, a => a());
        await updates.CheckAsync();

        client.Newer = "0.18.5";
        Assert.True(await updates.RestartAsync());
        Assert.Equal("0.18.5", client.PendingVersion);
    }

    [Fact]
    public void CheckStaysAvailableWhileADownloadedUpdateWaits() =>
        Assert.True(new UpdatesViewModel(new FakeUpdates { PendingVersion = "0.18.0" }, a => a()).CanCheck);

    [Fact]
    public async Task InstallOfADownloadedUpdateStillWorksOffline()
    {
        var client = new FakeUpdates { PendingVersion = "0.18.0", FindFails = new HttpRequestException("offline") };
        Assert.True(await new UpdatesViewModel(client, a => a()).RestartAsync());
        Assert.True(client.Applied);
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
    public async Task RestartDoesNothingWithoutAReadyUpdate()
    {
        var client = new FakeUpdates();
        Assert.False(await new UpdatesViewModel(client, a => a()).RestartAsync());
        Assert.False(client.Applied);
    }

    [Fact]
    public async Task AnUpdaterThatWontStartSaysWhyAndKeepsTheEditorOpen()
    {
        var client = new FakeUpdates { PendingVersion = "0.18.0", ApplyFails = new InvalidOperationException("Update.exe is missing.") };
        var updates = new UpdatesViewModel(client, a => a());

        Assert.False(await updates.RestartAsync());
        Assert.StartsWith("Couldn't install: Update.exe is missing.", updates.Status, StringComparison.Ordinal);
        Assert.True(updates.CanRestart);
    }

    private static string TempChannelFile() => Path.Combine(Directory.CreateTempSubdirectory("dc-chan").FullName, "update-channel.txt");

    [Fact]
    public void TheSavedChannelIsShownAndHandedToTheClient()
    {
        var path = TempChannelFile();
        new UpdateChannelStore(path).Save(UpdateChannel.ReleaseCandidates);
        var client = new FakeUpdates();
        var updates = new UpdatesViewModel(client, a => a(), new UpdateChannelStore(path));
        Assert.Equal(1, updates.ChannelIndex);
        Assert.Equal(UpdateChannel.ReleaseCandidates, client.Channel);
        Assert.Contains("Release candidates", updates.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void ChoosingAChannelSavesItAtOnceAndSwitchesTheClient()
    {
        var path = TempChannelFile();
        var client = new FakeUpdates();
        var updates = new UpdatesViewModel(client, a => a(), new UpdateChannelStore(path));
        Assert.Equal(0, updates.ChannelIndex);
        var changed = new List<string?>();
        updates.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        updates.ChannelIndex = 1;

        Assert.Equal(UpdateChannel.ReleaseCandidates, new UpdateChannelStore(path).Load());
        Assert.Equal(UpdateChannel.ReleaseCandidates, client.Channel);
        Assert.Contains(nameof(UpdatesViewModel.ChannelLabel), changed);
        Assert.Contains("Release candidates", updates.Status, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SwitchingChannelAsksAgainAndKeepsADownloadedUpdate()
    {
        var client = new FakeUpdates();
        var updates = new UpdatesViewModel(client, a => a(), new UpdateChannelStore(TempChannelFile()));
        await updates.CheckAsync();
        Assert.StartsWith("Up to date", updates.Status, StringComparison.Ordinal);
        updates.ChannelIndex = 1;
        Assert.StartsWith("Version 0.17.0, Release candidates", updates.Status, StringComparison.Ordinal);
        Assert.True(updates.CanCheck);

        client.Newer = "0.18.0";
        await updates.CheckAsync();
        updates.ChannelIndex = 0;
        Assert.True(updates.CanRestart);
        Assert.Contains("0.18.0", updates.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnsavableChoiceIsPutBackAndSaysWhy()
    {
        // A file where the folder should be: the save fails with an IOException.
        var blocker = Path.Combine(Directory.CreateTempSubdirectory("dc-chan").FullName, "x");
        File.WriteAllText(blocker, "");
        var client = new FakeUpdates();
        var updates = new UpdatesViewModel(client, a => a(), new UpdateChannelStore(Path.Combine(blocker, "update-channel.txt")));

        updates.ChannelIndex = 1;

        Assert.Equal(0, updates.ChannelIndex);
        Assert.Equal(UpdateChannel.Stable, client.Channel);
        Assert.StartsWith("Couldn't save the update channel", updates.Status, StringComparison.Ordinal);
    }

    [Fact]
    public void TheNoteSaysInPlainWordsWhatTheChoiceMeans()
    {
        var note = new UpdatesViewModel(null, a => a()).ChannelNote;
        Assert.Contains("rough edges", note, StringComparison.Ordinal);
        Assert.Contains("keeps the version you have", note, StringComparison.Ordinal);
        foreach (var banned in new[] { "prerelease", "pre-release", "semver", "node", "DAG", "OSD" })
            Assert.DoesNotContain(banned, note, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("0.52.0-rc.1+3fa9c1d", "0.52.0-rc.1")]
    [InlineData("0.52.0+3fa9c1d", "0.52.0")]
    [InlineData("0.52.0", "0.52.0")]
    [InlineData(null, null)]
    public void TheInstalledVersionLosesItsHashButKeepsTheCandidateSuffix(string? given, string? expected) =>
        Assert.Equal(expected, ShellViewModel.StripBuildMetadata(given));
}
