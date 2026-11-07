using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Providers.Android;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Tests;

public class AndroidMediaStepTests
{
    private sealed class ScriptedAdb(Func<string, AdbResult> shell) : IAdb
    {
        public List<string> Shells { get; } = [];

        public Task<AdbResult> RunAsync(string adb, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            if (args[0] == "connect")
                return Task.FromResult(new AdbResult(0, "connected to " + args[1] + "\n", ""));
            Shells.Add(args[^1]);
            return Task.FromResult(shell(args[^1]));
        }
    }

    private static readonly AndroidDevice Tv = new("tv", "192.168.1.50", new([]));

    private static StepContext Context(string step, params (string Name, string Value)[] with) =>
        Steps.Context(step, [("device", "tv"), .. with]) with { Library = new Library { Android = [Tv] } };

    private static AndroidRemote Remote(IAdb adb) => new(adb, TimeProvider.System);

    private static Task Run(IStepProvider p, StepContext c) => p.ExecuteAsync(c, TestContext.Current.CancellationToken);

    [Fact]
    public async Task SetVolumeReadsTheRangeThenSetsThePercentOfIt()
    {
        var adb = new ScriptedAdb(c => new(0, c.EndsWith("--get", StringComparison.Ordinal) ? "volume is 3 in range [0..15]\n" : "", ""));
        await Run(new AndroidSetVolumeProvider(Remote(adb)), Context("android.setVolume", ("stream", "media"), ("percent", "40")));
        Assert.Equal(["cmd media_session volume --stream 3 --get", "cmd media_session volume --stream 3 --set 6"], adb.Shells);
    }

    [Fact]
    public async Task SetVolumeFallsBackToTheOlderMediaTool()
    {
        var adb = new ScriptedAdb(c => c.StartsWith("cmd", StringComparison.Ordinal) ? new(0, "Unknown command: media_session\n", "")
            : new(0, c.EndsWith("--get", StringComparison.Ordinal) ? "volume is 1 in range [0..10]\n" : "", ""));
        await Run(new AndroidSetVolumeProvider(Remote(adb)), Context("android.setVolume", ("stream", "ring"), ("percent", "100")));
        Assert.Equal("media volume --stream 2 --set 10", adb.Shells[^1]);
    }

    [Fact]
    public async Task SetVolumeOnADeviceWithNeitherToolSaysSo()
    {
        var adb = new ScriptedAdb(_ => new(255, "", "/system/bin/sh: media: not found"));
        var failure = await Assert.ThrowsAsync<StepFailedException>(() => Run(new AndroidSetVolumeProvider(Remote(adb)), Context("android.setVolume", ("percent", "50"))));
        Assert.StartsWith("This Android device can't set its volume over adb", failure.Message);
    }

    [Theory]
    [InlineData("150")]
    [InlineData("-1")]
    public async Task SetVolumeRejectsAPercentOutsideTheRange(string percent)
    {
        var adb = new ScriptedAdb(_ => new(0, "", ""));
        await Assert.ThrowsAsync<StepFailedException>(() => Run(new AndroidSetVolumeProvider(Remote(adb)), Context("android.setVolume", ("percent", percent))));
        Assert.Empty(adb.Shells);
    }

    [Theory]
    [InlineData("up", 3, "input keyevent KEYCODE_VOLUME_UP && input keyevent KEYCODE_VOLUME_UP && input keyevent KEYCODE_VOLUME_UP")]
    [InlineData("down", 1, "input keyevent KEYCODE_VOLUME_DOWN")]
    public async Task ChangeVolumePressesTheKeyThatManyTimes(string direction, int times, string expected)
    {
        var adb = new ScriptedAdb(_ => new(0, "", ""));
        await Run(new AndroidChangeVolumeProvider(Remote(adb)), Context("android.changeVolume", ("direction", direction), ("times", times.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        Assert.Equal([expected], adb.Shells);
    }

    [Fact]
    public async Task MuteTriesTheAudioServiceThenTheSessionAdjustmentAndAnErrorLineWithExitZeroIsAFailure()
    {
        var adb = new ScriptedAdb(c => c.StartsWith("cmd audio", StringComparison.Ordinal) ? new(0, "Error: unknown command adj-unmute\n", "") : new(0, "", ""));
        await Run(new AndroidMuteProvider(Remote(adb)), Context("android.mute", ("muted", "false")));
        Assert.Equal(["cmd audio adj-unmute 3", "cmd media_session volume --stream 3 --adj unmute"], adb.Shells);
    }

    [Fact]
    public async Task MuteThatNoCommandCanDoFails()
    {
        var adb = new ScriptedAdb(_ => new(0, "Error: no valid volume adjustment\n", ""));
        var failure = await Assert.ThrowsAsync<StepFailedException>(() => Run(new AndroidMuteProvider(Remote(adb)), Context("android.mute")));
        Assert.StartsWith("This Android device can't mute over adb", failure.Message);
    }

    [Fact]
    public async Task AnErrorInsideAnAddressIsNotAnErrorLine()
    {
        var adb = new ScriptedAdb(_ => new(0, "Starting: Intent { dat=https://example.com/Error }\n", ""));
        await Run(new AndroidOpenProvider(Remote(adb)), Context("android.open", ("address", "https://example.com/Error")));
    }

    [Theory]
    [InlineData("pause", "KEYCODE_MEDIA_PAUSE")]
    [InlineData("fastForward", "KEYCODE_MEDIA_FAST_FORWARD")]
    [InlineData("playPause", "KEYCODE_MEDIA_PLAY_PAUSE")]
    public async Task ControlMediaSendsItsKey(string action, string code)
    {
        var adb = new ScriptedAdb(_ => new(0, "", ""));
        await Run(new AndroidMediaProvider(Remote(adb)), Context("android.media", ("action", action)));
        Assert.Equal(["input keyevent " + code], adb.Shells);
    }

    [Fact]
    public void EveryMediaChoiceHasAKeyAndACaption()
    {
        var action = ProviderCatalog.Find("android.media")!.Params.Single(p => p.Name == "action");
        Assert.Equal(action.Choices.OrderBy(x => x), AndroidMediaProvider.Keys.Keys.OrderBy(x => x));
        Assert.All(AndroidMediaProvider.Keys.Values, k => Assert.NotNull(AndroidKeys.Code(k)));
    }

    [Fact]
    public async Task BrightnessIsAPercentOf255WithAutoOff()
    {
        var adb = new ScriptedAdb(_ => new(0, "", ""));
        await Run(new AndroidSetBrightnessProvider(Remote(adb)), Context("android.setBrightness", ("percent", "50")));
        Assert.Equal(["settings put system screen_brightness_mode 0 && settings put system screen_brightness 128"], adb.Shells);
    }

    [Fact]
    public async Task OpenAddressViewsItAndRefusesAShellBreakingOne()
    {
        var adb = new ScriptedAdb(_ => new(0, "Starting: Intent\n", ""));
        await Run(new AndroidOpenProvider(Remote(adb)), Context("android.open", ("address", "https://example.com/a?b=1&c=2")));
        Assert.Equal(["am start -a android.intent.action.VIEW -d 'https://example.com/a?b=1&c=2'"], adb.Shells);
        await Assert.ThrowsAsync<StepFailedException>(() => Run(new AndroidOpenProvider(Remote(adb)), Context("android.open", ("address", "http://x'; reboot; '"))));
        await Assert.ThrowsAsync<StepFailedException>(() => Run(new AndroidOpenProvider(Remote(adb)), Context("android.open", ("address", "example.com"))));
        Assert.Single(adb.Shells);
    }

    [Fact]
    public async Task OpenAddressNoAppHandlesItFails()
    {
        var adb = new ScriptedAdb(_ => new(0, "Error: Activity not started, unable to resolve Intent\n", ""));
        var failure = await Assert.ThrowsAsync<StepFailedException>(() => Run(new AndroidOpenProvider(Remote(adb)), Context("android.open", ("address", "foo://bar"))));
        Assert.Contains("opens foo://bar", failure.Message);
    }

    [Fact]
    public async Task WakeSendsWakeup()
    {
        var adb = new ScriptedAdb(_ => new(0, "", ""));
        await Run(new AndroidWakeProvider(Remote(adb)), Context("android.wake"));
        Assert.Equal(["input keyevent KEYCODE_WAKEUP"], adb.Shells);
    }
}
