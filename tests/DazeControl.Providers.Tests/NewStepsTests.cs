using System.Diagnostics;
using DazeControl.Core.Model;
using DazeControl.Providers.Android;
using DazeControl.Providers.Display;
using DazeControl.Providers.Process;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Tests;

/// <summary>M18 (D-102): Close program, Stop app (Android), Turn screens off, all against fakes.</summary>
public class NewStepsTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // --- process.close ---

    private sealed class FakeTarget(bool exitsOnClose, bool canControl = true, bool exitsOnEnd = true) : ICloseTarget
    {
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> Calls { get; } = [];
        public bool HasExited => exited.Task.IsCompleted;
        public bool CanControl => canControl;

        public bool RequestClose()
        {
            Calls.Add("close");
            if (exitsOnClose)
                exited.TrySetResult();
            return true;
        }

        public bool EndTree()
        {
            Calls.Add("end");
            if (exitsOnEnd)
                exited.TrySetResult();
            return true;
        }

        public Task WaitForExitAsync(CancellationToken cancellationToken) => exited.Task.WaitAsync(cancellationToken);

        public void Dispose() => Calls.Add("dispose");
    }

    private sealed class FakeTargets(params FakeTarget[] running) : ICloseTargets
    {
        public List<string> Asked { get; } = [];

        public IReadOnlyList<ICloseTarget> Find(string name)
        {
            Asked.Add(name);
            return running;
        }
    }

    private static Task Close(ICloseTargets targets, TimeProvider time, params (string, string)[] with) =>
        new ProcessCloseProvider(targets, time).ExecuteAsync(Steps.Context("process.close", with), Token);

    [Fact]
    public async Task ClosesGracefullyWithoutEnding()
    {
        var target = new FakeTarget(exitsOnClose: true);
        var targets = new FakeTargets(target);

        await Close(targets, TimeProvider.System, ("program", @"C:\Apps\Notepad.exe"));

        Assert.Equal(["Notepad"], targets.Asked);
        Assert.Equal(["close", "dispose"], target.Calls);
    }

    [Fact]
    public async Task NotRunningIsSuccess()
    {
        var targets = new FakeTargets();
        await Close(targets, TimeProvider.System, ("program", "spotify"));
        Assert.Equal(["spotify"], targets.Asked);
    }

    [Fact]
    public async Task EndsItAfterTheForceTime()
    {
        var time = new ManualTime();
        var target = new FakeTarget(exitsOnClose: false);

        var step = Close(new FakeTargets(target), time, ("program", "steam"), ("force", "10s"));
        await time.WaitForTimerAsync();
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.False(step.IsCompleted);
        Assert.Equal(["close"], target.Calls);

        time.Advance(TimeSpan.FromSeconds(2));
        await step.WaitAsync(TimeSpan.FromSeconds(5), Token);
        Assert.Equal(["close", "end", "dispose"], target.Calls);
    }

    [Fact]
    public async Task FailsInWordsWhenItWillNotEnd()
    {
        var time = new ManualTime();
        var target = new FakeTarget(exitsOnClose: false, exitsOnEnd: false);

        var step = Close(new FakeTargets(target), time, ("program", "stubborn"), ("force", "1s"));
        await time.WaitForTimerAsync();
        time.Advance(TimeSpan.FromSeconds(2));
        await time.WaitForTimerAsync();
        time.Advance(TimeSpan.FromSeconds(6));

        var failure = await Assert.ThrowsAsync<StepFailedException>(() => step);
        Assert.Equal("stubborn didn't close.", failure.Message);
    }

    [Fact]
    public async Task ElevatedProgramFailsWithASentenceAndIsLeftAlone()
    {
        var target = new FakeTarget(exitsOnClose: true, canControl: false);

        var failure = await Assert.ThrowsAsync<StepFailedException>(() => Close(new FakeTargets(target), TimeProvider.System, ("program", "taskmgr")));

        Assert.Equal("taskmgr is running as administrator, so Daze Control can't close it.", failure.Message);
        Assert.Equal(["dispose"], target.Calls);
    }

    [Fact]
    public async Task StoppingTheStepStopsWaitingAndEndsNothing()
    {
        var target = new FakeTarget(exitsOnClose: false);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var step = new ProcessCloseProvider(new FakeTargets(target), TimeProvider.System)
            .ExecuteAsync(Steps.Context("process.close", ("program", "x"), ("force", "5m")), cancel.Token);
        await Task.Delay(50, Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => step);
        Assert.Equal(["close", "dispose"], target.Calls);
    }

    [Fact]
    public async Task ClosesARealProgramStartedHere()
    {
        // A uniquely named copy, so the name-based lookup can only ever find the process started here.
        var name = "dc-close-" + Guid.NewGuid().ToString("N")[..8];
        var dir = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(dir);
        var cmd = Path.Combine(dir, name + ".exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), cmd);
        using var started = System.Diagnostics.Process.Start(new ProcessStartInfo(cmd, "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            await Close(new WindowsCloseTargets(), TimeProvider.System, ("program", name), ("force", "1s"));
            Assert.True(started.WaitForExit(5000));
        }
        finally
        {
            if (!started.HasExited)
                started.Kill(entireProcessTree: true);
            started.WaitForExit(5000);
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    // --- android.stop ---

    private static readonly AndroidDevice Tv = new("tv", "192.168.1.50", new([]));

    private sealed class ScriptedAdb(Func<IReadOnlyList<string>, AdbResult> answer) : IAdb
    {
        public List<string> Calls { get; } = [];

        public Task<AdbResult> RunAsync(string adb, IReadOnlyList<string> args, CancellationToken cancellationToken)
        {
            Calls.Add(string.Join(' ', args));
            return Task.FromResult(args[0] == "connect" ? new AdbResult(0, "connected to 192.168.1.50:5555", "") : answer(args));
        }
    }

    private static StepContext Stop(string app) =>
        Steps.Context("android.stop", ("device", "tv"), ("app", app)) with { Library = new Library { Android = [Tv] } };

    [Fact]
    public async Task StopForceStopsThePackage()
    {
        var adb = new ScriptedAdb(_ => new AdbResult(0, "", ""));

        await new AndroidStopProvider(new AndroidRemote(adb, TimeProvider.System)).ExecuteAsync(Stop(" com.spotify.music "), Token);

        Assert.Equal(["connect 192.168.1.50:5555", "-s 192.168.1.50:5555 shell am force-stop com.spotify.music"], adb.Calls);
    }

    [Fact]
    public async Task StopRefusesANonPackageBeforeTouchingAdb()
    {
        var adb = new ScriptedAdb(_ => new AdbResult(0, "", ""));

        var failure = await Assert.ThrowsAsync<StepFailedException>(() =>
            new AndroidStopProvider(new AndroidRemote(adb, TimeProvider.System)).ExecuteAsync(Stop("x; reboot"), Token));

        Assert.Equal("\"x; reboot\" isn't an app's package name (like com.spotify.music).", failure.Message);
        Assert.Empty(adb.Calls);
    }

    [Fact]
    public async Task StopFailsInWordsWhenAdbRefuses()
    {
        var adb = new ScriptedAdb(_ => new AdbResult(1, "", "Security exception: denied"));

        var failure = await Assert.ThrowsAsync<StepFailedException>(() =>
            new AndroidStopProvider(new AndroidRemote(adb, TimeProvider.System)).ExecuteAsync(Stop("com.spotify.music"), Token));

        Assert.Equal("The Android device refused to stop com.spotify.music: Security exception: denied", failure.Message);
    }

    [Fact]
    public async Task StopUnknownDeviceNamesTheLibraryItem()
    {
        var context = Steps.Context("android.stop", ("device", "gone"), ("app", "com.a.b")) with { Library = new Library { Android = [Tv] } };
        await Assert.ThrowsAsync<StepFailedException>(() =>
            new AndroidStopProvider(new AndroidRemote(new ScriptedAdb(_ => new AdbResult(0, "", "")), TimeProvider.System)).ExecuteAsync(context, Token));
    }

    // --- display.screensOff ---

    private sealed class FakePower : IMonitorPower
    {
        public int Off { get; private set; }

        public void TurnOff() => Off++;
    }

    [Fact]
    public async Task ScreensOffWaitsTheGuardThenTurnsOffOnce()
    {
        var time = new ManualTime();
        var power = new FakePower();

        var step = new DisplayScreensOffProvider(power, time).ExecuteAsync(Steps.Context("display.screensOff"), Token);
        await time.WaitForTimerAsync();
        Assert.Equal(0, power.Off);
        time.Advance(DisplayScreensOffProvider.Guard);
        await step.WaitAsync(TimeSpan.FromSeconds(5), Token);

        Assert.Equal(1, power.Off);
    }

    [Fact]
    public async Task StoppedDuringTheGuardLeavesTheScreensOn()
    {
        var time = new ManualTime();
        var power = new FakePower();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);

        var step = new DisplayScreensOffProvider(power, time).ExecuteAsync(Steps.Context("display.screensOff"), cancel.Token);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => step);
        Assert.Equal(0, power.Off);
    }
}
