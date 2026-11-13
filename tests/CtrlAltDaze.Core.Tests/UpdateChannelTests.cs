using CtrlAltDaze.Core.Backup;
using CtrlAltDaze.Core.Updates;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;

namespace CtrlAltDaze.Core.Tests;

/// <summary>D-108: the channel file, which releases each channel asks for, and Velopack's version order across RC and stable.</summary>
public sealed class UpdateChannelTests
{
    private sealed class Files : IBackupFiles
    {
        public Dictionary<string, byte[]> Data = new(StringComparer.OrdinalIgnoreCase);
        public bool FailWrites;
        public bool Exists(string path) => Data.ContainsKey(path);
        public byte[] ReadAll(string path) => Data[path];
        public bool TryWriteNew(string path, byte[] data) => Data.TryAdd(path, data);
        public void Delete(string path) => Data.Remove(path);
        public byte[]? TryReadAll(string path) => Data.GetValueOrDefault(path);
        public void ReplaceAtomic(string path, byte[] data)
        {
            if (FailWrites) throw new IOException("disk full");
            Data[path] = data;
        }
        public IReadOnlyList<string> ListFiles(string folder) => [];
    }

    [Theory]
    [InlineData(null, UpdateChannel.Stable)]
    [InlineData("", UpdateChannel.Stable)]
    [InlineData("channel=stable\n", UpdateChannel.Stable)]
    [InlineData("channel=rc\n", UpdateChannel.ReleaseCandidates)]
    [InlineData("  CHANNEL = RC \r\n", UpdateChannel.ReleaseCandidates)]
    [InlineData("channel=beta\n", UpdateChannel.Stable)]
    [InlineData("rc\n", UpdateChannel.Stable)]
    [InlineData("\0\0garbage", UpdateChannel.Stable)]
    public void ParseFallsBackToStable(string? text, UpdateChannel expected) =>
        Assert.Equal(expected, UpdateChannelStore.Parse(text));

    [Fact]
    public void FromWordTakesStableAndRcOnly()
    {
        Assert.Equal(UpdateChannel.Stable, UpdateChannels.FromWord("Stable"));
        Assert.Equal(UpdateChannel.ReleaseCandidates, UpdateChannels.FromWord("RC"));
        Assert.Null(UpdateChannels.FromWord("prerelease"));
        Assert.Null(UpdateChannels.FromWord(null));
    }

    [Fact]
    public void SavedChoiceComesBackAndAMissingFileIsStable()
    {
        var files = new Files();
        var store = new UpdateChannelStore("/c/update-channel.txt", files);
        Assert.Equal(UpdateChannel.Stable, store.Load());
        store.Save(UpdateChannel.ReleaseCandidates);
        Assert.Equal("channel=rc\n", System.Text.Encoding.UTF8.GetString(files.Data["/c/update-channel.txt"]));
        Assert.Equal(UpdateChannel.ReleaseCandidates, new UpdateChannelStore("/c/update-channel.txt", files).Load());
        store.Save(UpdateChannel.Stable);
        Assert.Equal(UpdateChannel.Stable, store.Load());
    }

    [Fact]
    public void AFailedSaveThrowsAndKeepsTheOldChoice()
    {
        var files = new Files();
        var store = new UpdateChannelStore("/c/update-channel.txt", files);
        store.Save(UpdateChannel.ReleaseCandidates);
        files.FailWrites = true;
        Assert.Throws<IOException>(() => store.Save(UpdateChannel.Stable));
        Assert.Equal(UpdateChannel.ReleaseCandidates, store.Load());
    }

    [Fact]
    public void OnlyTheReleaseCandidateChannelAsksForPreReleases()
    {
        Assert.False(UpdateChannel.Stable.IncludesPreReleases());
        Assert.True(UpdateChannel.ReleaseCandidates.IncludesPreReleases());
    }

    [Fact]
    public void ChannelsHaveWordsTheUserCanRead()
    {
        Assert.Equal("Stable", UpdateChannel.Stable.Label());
        Assert.Equal("Release candidates", UpdateChannel.ReleaseCandidates.Label());
        Assert.DoesNotContain("pre", UpdateChannel.ReleaseCandidates.Label(), StringComparison.OrdinalIgnoreCase);
    }

    // ---- version order, through the real UpdateManager over a fake feed ----

    private sealed class Feed(params string[] versions) : IUpdateSource
    {
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalFullRelease = null) =>
            Task.FromResult(new VelopackAssetFeed
            {
                Assets = [.. versions.Select(v => new VelopackAsset
                {
                    PackageId = "CtrlAltDaze", Version = SemanticVersion.Parse(v),
                    Type = VelopackAssetType.Full, FileName = $"CtrlAltDaze-{v}-full.nupkg", SHA1 = "", Size = 1,
                })],
            });

        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken) =>
            Task.CompletedTask;
    }

    private static async Task<string?> Newest(string current, params string[] feed)
    {
        var dir = Directory.CreateTempSubdirectory("dc-upd").FullName;
        try
        {
            var locator = new TestVelopackLocator("CtrlAltDaze", current, dir, logger: null);
            var manager = new UpdateManager(new Feed(feed), new UpdateOptions(), locator);
            return (await manager.CheckForUpdatesAsync())?.TargetFullRelease.Version.ToString();
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task AReleaseCandidateIsOfferedToAStableCopy() =>
        Assert.Equal("0.52.0-rc.1", await Newest("0.51.0", "0.51.0", "0.52.0-rc.1"));

    [Fact]
    public async Task AStableReleaseSupersedesItsReleaseCandidate() =>
        Assert.Equal("0.52.0", await Newest("0.52.0-rc.1", "0.52.0-rc.1", "0.52.0"));

    [Fact]
    public async Task AStableReleaseBeatsEveryCandidateOfItsNumber() =>
        Assert.Equal("0.52.0", await Newest("0.51.0", "0.52.0-rc.1", "0.52.0-rc.2", "0.52.0"));

    [Fact]
    public async Task ALaterCandidateReplacesAnEarlierOne() =>
        Assert.Equal("0.52.0-rc.2", await Newest("0.52.0-rc.1", "0.52.0-rc.1", "0.52.0-rc.2"));

    [Fact]
    public async Task ACandidateNewerThanTheLatestStableIsNotDowngraded() =>
        Assert.Null(await Newest("0.52.0-rc.1", "0.51.0", "0.51.1"));

    [Fact]
    public void TheClientRebuildsItsManagerWhenTheChannelChanges()
    {
        var asked = new List<UpdateChannel>();
        var dir = Directory.CreateTempSubdirectory("dc-upd").FullName;
        try
        {
            var client = new VelopackUpdateClient(UpdateChannel.Stable, channel =>
            {
                asked.Add(channel);
                return new UpdateManager(new Feed(), new UpdateOptions(), new TestVelopackLocator("CtrlAltDaze", "0.51.0", dir, logger: null));
            });
            client.Channel = UpdateChannel.ReleaseCandidates;
            client.Channel = UpdateChannel.ReleaseCandidates;
            Assert.Equal([UpdateChannel.Stable, UpdateChannel.ReleaseCandidates], asked);
            Assert.Equal(UpdateChannel.ReleaseCandidates, client.Channel);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private sealed class GatedFeed(Task gate, string version) : IUpdateSource
    {
        public async Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalFullRelease = null)
        {
            await gate.ConfigureAwait(false);
            return await new Feed(version).GetReleaseFeed(logger, appId, channel, stagingId, latestLocalFullRelease).ConfigureAwait(false);
        }

        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken) =>
            Task.CompletedTask;
    }

    [Fact]
    public async Task ACheckStillRunningWhenTheChannelChangesFindsNothingAndDownloadsNothing()
    {
        var dir = Directory.CreateTempSubdirectory("dc-upd").FullName;
        try
        {
            var gate = new TaskCompletionSource();
            var client = new VelopackUpdateClient(UpdateChannel.ReleaseCandidates, channel => new UpdateManager(
                channel == UpdateChannel.ReleaseCandidates ? new GatedFeed(gate.Task, "0.52.0-rc.1") : new Feed("0.51.0"),
                new UpdateOptions(), new TestVelopackLocator("CtrlAltDaze", "0.51.0", dir, logger: null)));
            var check = client.FindAsync(CancellationToken.None);
            client.Channel = UpdateChannel.Stable;
            gate.SetResult();
            Assert.Null(await check);
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.DownloadAsync(null, CancellationToken.None));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private sealed class SwitchingFeed : IUpdateSource
    {
        public string[] Versions { get; set; } = [];
        public bool Offline { get; set; }
        public List<string> Downloaded { get; } = [];

        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalFullRelease = null) =>
            Offline ? Task.FromException<VelopackAssetFeed>(new HttpRequestException("offline")) : Task.FromResult(new VelopackAssetFeed { Assets = [.. Versions.Select(v => new VelopackAsset { PackageId = "CtrlAltDaze", Version = SemanticVersion.Parse(v), Type = VelopackAssetType.Full, FileName = $"CtrlAltDaze-{v}-full.nupkg", SHA1 = "5BA93C9DB0CFF93F52B521D7420E43F6EDA2784F", Size = 1 })] });

        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken)
        {
            Downloaded.Add(releaseEntry.Version.ToString());
            File.WriteAllBytes(localFile, [0]);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RefreshBeforeApplyDownloadsAReleaseThatAppearedAfterTheFirstCheck()
    {
        var dir = Directory.CreateTempSubdirectory("dc-upd").FullName;
        try
        {
            var feed = new SwitchingFeed { Versions = ["0.52.0"] };
            var client = new VelopackUpdateClient(UpdateChannel.Stable, _ => new UpdateManager(feed, new UpdateOptions(),
                new TestVelopackLocator("CtrlAltDaze", "0.51.0", dir, logger: null)));
            Assert.Equal("0.52.0", await client.FindAsync(CancellationToken.None));
            await client.DownloadAsync(null, CancellationToken.None);

            feed.Versions = ["0.52.0", "0.53.0"];
            await client.RefreshBeforeApplyAsync(CancellationToken.None);

            Assert.Equal(["0.52.0", "0.53.0"], feed.Downloaded);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task RefreshBeforeApplyOfflineDownloadsNothingAndDoesNotThrow()
    {
        var dir = Directory.CreateTempSubdirectory("dc-upd").FullName;
        try
        {
            var feed = new SwitchingFeed { Offline = true };
            var client = new VelopackUpdateClient(UpdateChannel.Stable, _ => new UpdateManager(feed, new UpdateOptions(),
                new TestVelopackLocator("CtrlAltDaze", "0.51.0", dir, logger: null)));
            await client.RefreshBeforeApplyAsync(CancellationToken.None);
            Assert.Empty(feed.Downloaded);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}
