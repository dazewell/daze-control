using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Providers.Android;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Tests;

public class AndroidProviderTests
{
    /// <summary>Answers <c>connect</c> with the scripted lines in turn (the last repeats), any shell with success.</summary>
    private sealed class FakeAdb(params string[] connectAnswers) : IAdb
    {
        private int _connects;

        public List<string> Calls { get; } = [];

        public int ShellExitCode { get; init; }

        public Task<AdbResult> RunAsync(string adb, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            Calls.Add($"{adb} {string.Join(' ', args)}");
            if (args[0] == "connect")
            {
                var answer = connectAnswers.Length == 0 ? "connected to " + args[1] : connectAnswers[Math.Min(_connects++, connectAnswers.Length - 1)];
                return Task.FromResult(new AdbResult(0, answer + "\n", ""));
            }
            return Task.FromResult(new AdbResult(ShellExitCode, "", ShellExitCode == 0 ? "" : "error: device offline"));
        }
    }

    private static readonly AndroidDevice Tv = new("tv", "192.168.1.50",
        new([KeyValuePair.Create("hdmi2", new ValueList<string>(["down", "down"])), KeyValuePair.Create("pc", new ValueList<string>(["up"]))]));

    private static StepContext Context(string step, params (string Name, string Value)[] with)
    {
        var context = Steps.Context(step, [("device", "tv"), .. with]);
        return context with { Library = new Library { Android = [Tv, Tv with { Key = "den", Address = "10.0.0.9:5556", Adb = @"C:\tools\adb.exe" }] } };
    }

    private static Task Run(IStepProvider provider, StepContext context) =>
        provider.ExecuteAsync(context, TestContext.Current.CancellationToken);

    [Fact]
    public async Task InputIsOneChainedShellCallAfterConnecting()
    {
        var adb = new FakeAdb();

        await Run(new AndroidInputProvider(new AndroidRemote(adb, TimeProvider.System)), Context("android.input", ("input", "hdmi2")));

        Assert.Equal(
        [
            "adb connect 192.168.1.50:5555",
            "adb -s 192.168.1.50:5555 shell input keyevent KEYCODE_WAKEUP && input keyevent KEYCODE_TV_INPUT"
                + " && input keyevent KEYCODE_DPAD_DOWN && input keyevent KEYCODE_DPAD_DOWN && input keyevent KEYCODE_DPAD_CENTER",
        ], adb.Calls);
    }

    [Fact]
    public async Task DeviceWithItsOwnAdbAndPortUsesThem()
    {
        var adb = new FakeAdb("already connected to 10.0.0.9:5556");
        var context = Context("android.sleep") with { Node = new StepNode("s1", "android.sleep") { With = new([KeyValuePair.Create("device", (ParamValue)"den")]) } };

        await Run(new AndroidSleepProvider(new AndroidRemote(adb, TimeProvider.System)), context);

        Assert.Equal([@"C:\tools\adb.exe connect 10.0.0.9:5556", @"C:\tools\adb.exe -s 10.0.0.9:5556 shell input keyevent KEYCODE_SLEEP"], adb.Calls);
    }

    [Fact]
    public async Task UnknownInputNamesTheSavedOnes()
    {
        var adb = new FakeAdb();
        var failure = await Assert.ThrowsAsync<StepFailedException>(
            () => Run(new AndroidInputProvider(new AndroidRemote(adb, TimeProvider.System)), Context("android.input", ("input", "hdmi9"))));

        Assert.Equal("The Android device \"tv\" has no input called \"hdmi9\" (hdmi2, pc).", failure.Message);
        Assert.Empty(adb.Calls);
    }

    [Fact]
    public async Task KeysAreNormalisedAndSentInOrder()
    {
        var adb = new FakeAdb();
        var context = Context("android.keys").WithList("keys", "home", "dpad_down", "KEYCODE_MEDIA_PLAY_PAUSE");

        await Run(new AndroidKeysProvider(new AndroidRemote(adb, TimeProvider.System)), context);

        Assert.Equal("adb -s 192.168.1.50:5555 shell input keyevent KEYCODE_HOME && input keyevent KEYCODE_DPAD_DOWN"
            + " && input keyevent KEYCODE_MEDIA_PLAY_PAUSE", adb.Calls[^1]);
    }

    [Theory]
    [InlineData("home; reboot")]
    [InlineData("home && reboot")]
    [InlineData("")]
    public async Task KeysThatArentKeyNamesSendNothing(string key)
    {
        var adb = new FakeAdb();
        await Assert.ThrowsAsync<StepFailedException>(
            () => Run(new AndroidKeysProvider(new AndroidRemote(adb, TimeProvider.System)), Context("android.keys").WithList("keys", "back", key)));
        Assert.Empty(adb.Calls);
    }

    [Fact]
    public async Task ConnectRetriesThenSucceeds()
    {
        var adb = new FakeAdb("failed to connect to 192.168.1.50:5555", "connected to 192.168.1.50:5555");

        await Run(new AndroidSleepProvider(new AndroidRemote(adb, TimeProvider.System)), Context("android.sleep"));

        Assert.Equal(3, adb.Calls.Count); // connect, connect, shell
    }

    [Fact]
    public async Task ConnectGivesUpAfterThreeTriesInPlainWords()
    {
        var adb = new FakeAdb("failed to connect to '192.168.1.50:5555': Connection refused");

        var failure = await Assert.ThrowsAsync<StepFailedException>(
            () => Run(new AndroidSleepProvider(new AndroidRemote(adb, TimeProvider.System)), Context("android.sleep")));

        Assert.Equal(3, adb.Calls.Count);
        Assert.Equal("Couldn't reach the Android device at 192.168.1.50:5555 (failed to connect to '192.168.1.50:5555': Connection refused). "
            + "Is it on, with network debugging turned on?", failure.Message);
    }

    [Fact]
    public async Task ConnectStepOnlyConnectsAndAlreadyConnectedIsSuccess()
    {
        var adb = new FakeAdb("already connected to 192.168.1.50:5555");

        await Run(new AndroidConnectProvider(new AndroidRemote(adb, TimeProvider.System)), Context("android.connect"));

        Assert.Equal(["adb connect 192.168.1.50:5555"], adb.Calls);
    }

    [Fact]
    public async Task ConnectStepFailedAuthenticationSaysToAcceptThePrompt()
    {
        var adb = new FakeAdb("failed to authenticate to 192.168.1.50:5555");

        var failure = await Assert.ThrowsAsync<StepFailedException>(
            () => Run(new AndroidConnectProvider(new AndroidRemote(adb, TimeProvider.System)), Context("android.connect")));

        Assert.Contains("Accept the debugging prompt", failure.Message);
        Assert.Equal(3, adb.Calls.Count);
    }

    [Fact]
    public async Task ShellErrorFailsTheStep()
    {
        var adb = new FakeAdb { ShellExitCode = 1 };
        var failure = await Assert.ThrowsAsync<StepFailedException>(
            () => Run(new AndroidSleepProvider(new AndroidRemote(adb, TimeProvider.System)), Context("android.sleep")));
        Assert.Equal("The Android device refused the keys: error: device offline", failure.Message);
    }

    [Fact]
    public async Task MissingAdbSaysHowToGetIt()
    {
        var failure = await Assert.ThrowsAsync<StepFailedException>(() => new AdbProcess().RunAsync(
            @"C:\nowhere\adb.exe", ["devices"], TestContext.Current.CancellationToken));
        Assert.StartsWith(@"Couldn't start C:\nowhere\adb.exe", failure.Message);
    }

    [Fact]
    public void AdbDevicesAreParsed()
    {
        var devices = SystemDevices.AdbDevices("* daemon started successfully\nList of devices attached\n192.168.1.50:5555\tdevice\nemulator-5554\toffline\n\n");
        Assert.Equal(["192.168.1.50:5555 connected", "emulator-5554 offline"], devices.Select(d => $"{d.Name} {d.Detail}"));
    }

    [Fact]
    public void EverySuggestedKeyIsSentAsItsTableCode() =>
        Assert.All(AndroidKeys.Names, name => Assert.Equal(AndroidKeys.Code(name), AndroidRemote.KeyCode(name)));
}
