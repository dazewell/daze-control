using DazeControl.Core.Model;
using DazeControl.Providers.Android;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Tests;

public class AndroidAppsTests
{
    private const string Listing = """
              packageName=com.android.tv.settings
              labelRes=0x7f1106d5 nonLocalizedLabel=null icon=0x0 banner=0x0
              packageName=com.fitstars.app
              labelRes=0x0 nonLocalizedLabel=Fit Stars icon=0x7f0e0000 banner=0x7f0800e1
              packageName=com.vk.tv
              labelRes=0x7f1106d5 nonLocalizedLabel=null icon=0x0 banner=0x0
              packageName=org.xbmc.kodi
              labelRes=0x7f1106d5 nonLocalizedLabel=null icon=0x0 banner=0x0
              packageName=com.vk.tv
              labelRes=0x7f1106d5 nonLocalizedLabel=null icon=0x0 banner=0x0
              packageName=bad;rm -rf
              labelRes=0x0 nonLocalizedLabel=Evil icon=0x0
            """;

    private sealed class ScriptedAdb(Func<IReadOnlyList<string>, AdbResult> answer) : IAdb
    {
        public List<string> Calls { get; } = [];

        public Task<AdbResult> RunAsync(string adb, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            Calls.Add(string.Join(' ', args));
            return Task.FromResult(args[0] == "connect" ? new AdbResult(0, "connected to " + args[1] + "\n", "") : answer(args));
        }
    }

    private static readonly AndroidDevice Tv = new("tv", "192.168.1.50", new([]));

    private static StepContext Context(string app) =>
        Steps.Context("android.start", [("device", "tv"), ("app", app)]) with { Library = new Library { Android = [Tv] } };

    [Fact]
    public void AppsAreNamedAndSortedByName()
    {
        var apps = AndroidAppList.Parse(Listing);

        Assert.Equal(["Fit Stars", "Kodi", "Settings", "Vk"], apps.Select(a => a.Name));
        Assert.Equal(["com.fitstars.app", "org.xbmc.kodi", "com.android.tv.settings", "com.vk.tv"], apps.Select(a => a.Package));
    }

    [Theory]
    [InlineData("com.netflix.ninja", "Netflix")]
    [InlineData("com.softmedia.receiver.castapp", "Castapp")]
    [InlineData("com.example.tv", "Example")]
    [InlineData("tv", "Tv")]
    public void UnknownNamesAreReadFromThePackage(string package, string name) => Assert.Equal(name, AndroidAppList.Guess(package));

    [Fact]
    public void PackageListFallbackKeepsOnlyPackages()
    {
        var apps = AndroidAppList.ParsePackages("package:org.xbmc.kodi\r\nnoise\r\npackage:a;b\r\npackage:com.vk.tv\r\n");

        Assert.Equal(["org.xbmc.kodi", "com.vk.tv"], apps.Select(a => a.Package));
    }

    [Fact]
    public async Task StartTriesThePhoneLauncherFirst()
    {
        var adb = new ScriptedAdb(_ => new AdbResult(0, "Starting: Intent { act=android.intent.action.MAIN }\n", ""));

        await new AndroidStartProvider(new AndroidRemote(adb, TimeProvider.System)).ExecuteAsync(Context("com.spotify.music"), TestContext.Current.CancellationToken);

        Assert.Equal(["connect 192.168.1.50:5555",
            "-s 192.168.1.50:5555 shell am start -a android.intent.action.MAIN -c android.intent.category.LAUNCHER -p com.spotify.music"], adb.Calls);
    }

    [Fact]
    public async Task StartFallsBackToTheTvLauncher()
    {
        var adb = new ScriptedAdb(args => args[^1].Contains("LEANBACK", StringComparison.Ordinal)
            ? new AdbResult(0, "Starting: Intent {}\n", "")
            : new AdbResult(0, "Error: Activity not started, unable to resolve Intent {}\n", ""));

        await new AndroidStartProvider(new AndroidRemote(adb, TimeProvider.System)).ExecuteAsync(Context("org.xbmc.kodi"), TestContext.Current.CancellationToken);

        Assert.Equal(3, adb.Calls.Count);
        Assert.Contains("LEANBACK_LAUNCHER -p org.xbmc.kodi", adb.Calls[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartFailsInPlainWordsWhenNeitherLauncherResolves()
    {
        var adb = new ScriptedAdb(_ => new AdbResult(0, "Error: Activity not started, unable to resolve Intent {}\n", ""));

        var e = await Assert.ThrowsAsync<StepFailedException>(() =>
            new AndroidStartProvider(new AndroidRemote(adb, TimeProvider.System)).ExecuteAsync(Context("com.gone.app"), TestContext.Current.CancellationToken));

        Assert.Contains("com.gone.app isn't installed", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartRefusesAPackageThatIsNotOneBeforeTouchingAdb()
    {
        var adb = new ScriptedAdb(_ => new AdbResult(0, "", ""));

        await Assert.ThrowsAsync<StepFailedException>(() =>
            new AndroidStartProvider(new AndroidRemote(adb, TimeProvider.System)).ExecuteAsync(Context("x; reboot"), TestContext.Current.CancellationToken));

        Assert.Empty(adb.Calls);
    }

    private static (AndroidAppLister Lister, ScriptedAdb Adb, string Dir) Lister(Func<IReadOnlyList<string>, AdbResult> answer)
    {
        var dir = Path.Combine(Path.GetTempPath(), "dc-apps-" + Guid.NewGuid().ToString("N"));
        var adb = new ScriptedAdb(answer);
        return (new AndroidAppLister(new AndroidRemote(adb, TimeProvider.System), adb, new AndroidAppCache(dir)), adb, dir);
    }

    [Fact]
    public async Task ListWithoutRefreshNeverTouchesAdbAndRefreshSavesTheList()
    {
        var (lister, adb, dir) = Lister(_ => new AdbResult(0, Listing, ""));
        try
        {
            var cancel = TestContext.Current.CancellationToken;
            Assert.Empty(await lister.ListAsync("192.168.1.50", null, refresh: false, cancel));
            Assert.Empty(adb.Calls);

            var fresh = await lister.ListAsync("192.168.1.50", null, refresh: true, cancel);
            var cached = await lister.ListAsync("192.168.1.50:5555", null, refresh: false, cancel);

            Assert.Equal(4, fresh.Count);
            Assert.Equal(fresh, cached);
            Assert.Equal(2, adb.Calls.Count); // connect + one listing; the cached read added none
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task ListFallsBackToThePackageListAndKeepsTheOldCacheOnFailure()
    {
        var failing = false;
        var (lister, _, dir) = Lister(args => failing ? new AdbResult(1, "", "error: device offline")
            : args[^1].StartsWith("pm ", StringComparison.Ordinal) ? new AdbResult(0, "package:org.xbmc.kodi\n", "") : new AdbResult(0, "", ""));
        try
        {
            var cancel = TestContext.Current.CancellationToken;
            Assert.Equal(["org.xbmc.kodi"], (await lister.ListAsync("tv.lan", null, true, cancel)).Select(a => a.Package));

            failing = true;
            await Assert.ThrowsAsync<StepFailedException>(() => lister.ListAsync("tv.lan", null, true, cancel));

            Assert.Equal(["org.xbmc.kodi"], (await lister.ListAsync("tv.lan", null, false, cancel)).Select(a => a.Package));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }
}
