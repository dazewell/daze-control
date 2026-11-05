using DazeControl.Core.Input;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Ipc;
using DazeControl.Runtime.Control;
using DazeControl.Runtime.Lifecycle;
using DazeControl.Runtime.Triggers;

namespace DazeControl.Runtime.Tests;

/// <summary>T12.6: the seams around the pad reader (DESIGN §3.4, §3.6), no hardware.</summary>
public sealed class GamepadServiceTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class FakeBackend : IPadBackend
    {
        public int Starts, Halts;
        public bool Fast;
        public bool Running;
        public List<PadInfo> Pads = [];

        public event Action<PadReading>? Reading;

        public void Start()
        {
            Starts++;
            Running = true;
        }

        public void Halt()
        {
            Halts++;
            Running = false;
        }

        public void SetFast(bool fast) => Fast = fast;

        public IReadOnlyList<PadInfo> List() => Pads;

        public void Dispose()
        {
        }

        public void Press(GamepadButtons buttons) => Reading?.Invoke(new PadReading(buttons));
    }

    private sealed class FakeXInput : IXInput
    {
        public readonly Dictionary<int, XInputState> Pads = [];
        public int Asked;

        public bool TryGetState(int slot, out XInputState state)
        {
            Asked++;
            return Pads.TryGetValue(slot, out state);
        }

        public string? Battery(int slot) => "wired";
    }

    private readonly FakeBackend _backend = new() { Pads = [new PadInfo(0, null)] };

    public void Dispose() => _backend.Dispose();

    private GamepadService Service() => new(_backend, TimeProvider.System);

    [Fact]
    public void NothingStartsUntilSomeoneListens()
    {
        using var service = Service();

        Assert.Equal(0, _backend.Starts);
        Assert.False(service.Running);
        Assert.Single(service.List()); // devices gamepads lists a pad with no chord and no reader
        Assert.Equal(0, _backend.Starts);
    }

    [Fact]
    public void TheFirstListenerStartsTheReaderAndTheLastOneStopsIt()
    {
        using var service = Service();
        var a = service.Listen(_ => { });
        var b = service.Listen(_ => { }, fast: true);

        Assert.Equal(1, _backend.Starts);
        Assert.True(_backend.Fast);

        a.Dispose();
        Assert.True(_backend.Running);
        b.Dispose();
        Assert.False(_backend.Running);
        Assert.Equal(1, _backend.Halts);
        b.Dispose(); // twice is harmless
        Assert.Equal(1, _backend.Halts);
    }

    [Fact]
    public void ReadingsReachEveryListenerAndStopWithTheReader()
    {
        using var service = Service();
        List<GamepadButtons> one = [], two = [];
        using var a = service.Listen(r => one.Add(r.Buttons));
        var b = service.Listen(r => two.Add(r.Buttons));

        _backend.Press(GamepadButtons.A);
        b.Dispose();
        _backend.Press(GamepadButtons.B);

        Assert.Equal([GamepadButtons.A, GamepadButtons.B], one);
        Assert.Equal([GamepadButtons.A], two);
    }

    [Fact]
    public void AFailingListenerDoesNotSilenceTheOthers()
    {
        using var service = Service();
        var heard = 0;
        using var bad = service.Listen(_ => throw new InvalidOperationException("boom"));
        using var good = service.Listen(_ => heard++);

        _backend.Press(GamepadButtons.A);

        Assert.Equal(1, heard);
    }

    [Fact]
    public void SleepStopsTheReaderAndResumeBringsItBackForTheSameListeners()
    {
        using var service = Service();
        var heard = 0;
        using var listening = service.Listen(_ => heard++);

        service.Suspend();
        Assert.False(_backend.Running);
        service.Resume();
        Assert.True(_backend.Running);
        _backend.Press(GamepadButtons.A);

        Assert.Equal(2, _backend.Starts);
        Assert.Equal(1, heard);
    }

    [Fact]
    public void ResumeWithNobodyListeningStartsNothing()
    {
        using var service = Service();

        service.Suspend();
        service.Resume();

        Assert.Equal(0, _backend.Starts);
    }

    [Fact]
    public async Task RecordingStartsTheReaderWithNoChordConfiguredAndAnswersTheFirstChord()
    {
        using var service = Service();
        List<bool> silenced = [];
        service.RecordingChanged += silenced.Add;

        var recording = service.RecordAsync(TestContext.Current.CancellationToken);
        Assert.True(_backend.Running);
        _backend.Press(GamepadButtons.None);
        _backend.Press(GamepadButtons.Menu);
        _backend.Press(GamepadButtons.Menu | GamepadButtons.View);
        _backend.Press(GamepadButtons.View); // Menu let go: the chord is View+Menu, in canonical order

        var answer = await recording.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.Equal("View+Menu", answer.Chord);
        Assert.False(_backend.Running); // released on the answer
        Assert.Equal([true, false], silenced);
    }

    [Fact]
    public async Task RecordingIgnoresButtonsHeldFromBeforeItBegan()
    {
        using var service = Service();
        var recording = service.RecordAsync(TestContext.Current.CancellationToken);

        _backend.Press(GamepadButtons.A);            // already down: its release is not a chord
        _backend.Press(GamepadButtons.None);
        _backend.Press(GamepadButtons.LB | GamepadButtons.RB);
        _backend.Press(GamepadButtons.RB);

        Assert.Equal("LB+RB", (await recording.WaitAsync(Wait, TestContext.Current.CancellationToken)).Chord);
    }

    [Fact]
    public async Task RecordingWithNoPadAnswersNoControllerAndStartsNothing()
    {
        _backend.Pads = [];
        using var service = Service();

        var answer = await service.RecordAsync(TestContext.Current.CancellationToken);

        Assert.Equal(GamepadRecord.NoController, answer.Error);
        Assert.Equal(0, _backend.Starts);
    }

    [Fact]
    public async Task RecordingTimesOutAndReleasesTheReader()
    {
        var time = new ManualTime();
        using var service = new GamepadService(_backend, time);
        List<bool> silenced = [];
        service.RecordingChanged += silenced.Add;

        var recording = service.RecordAsync(TestContext.Current.CancellationToken);
        Assert.True(_backend.Running);
        time.Advance(GamepadService.RecordTimeout + TimeSpan.FromSeconds(1));

        Assert.Equal(GamepadRecord.TimedOut, (await recording.WaitAsync(Wait, TestContext.Current.CancellationToken)).Error);
        Assert.False(_backend.Running);
        Assert.Equal([true, false], silenced);
    }

    [Fact]
    public async Task RecordingEndsWhenTheClientGoes()
    {
        using var service = Service();
        using var client = new CancellationTokenSource();
        List<bool> silenced = [];
        service.RecordingChanged += silenced.Add;

        var recording = service.RecordAsync(client.Token);
        await client.CancelAsync();

        Assert.Equal(GamepadRecord.Stopped, (await recording.WaitAsync(Wait, TestContext.Current.CancellationToken)).Error);
        Assert.False(_backend.Running);
        Assert.Equal([true, false], silenced);
    }

    [Fact]
    public async Task ARecordingKeepsTheReaderRunningForAChordListenerAfterwards()
    {
        using var service = Service();
        using var chords = service.Listen(_ => { });
        var recording = service.RecordAsync(TestContext.Current.CancellationToken);
        _backend.Press(GamepadButtons.None);
        _backend.Press(GamepadButtons.A | GamepadButtons.B);
        _backend.Press(GamepadButtons.B);
        await recording.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.True(_backend.Running);
        Assert.Equal(1, _backend.Starts);
    }

    // ---- PadPoller: the reader's logic, step by step -------------------------------------------------------

    [Fact]
    public void WithNoPadThePollerNeverPolls()
    {
        var api = new FakeXInput();
        var poller = new PadPoller(api);

        Assert.False(poller.Scan());
        var asked = api.Asked;

        Assert.Null(poller.Step(fast: false));
        Assert.Null(poller.Step(fast: true));
        Assert.Equal(0, poller.Polls);
        Assert.Equal(asked, api.Asked); // an empty slot is slow to ask: not asked again until a rescan
    }

    [Fact]
    public void ThePollerOnlyAsksConnectedSlotsAndGoesBackToSleepWhenTheLastPadGoes()
    {
        var api = new FakeXInput();
        api.Pads[2] = default;
        var poller = new PadPoller(api);
        List<PadReading> readings = [];
        poller.Reading += readings.Add;
        poller.Scan();
        var asked = api.Asked;

        Assert.Equal(PadPoller.SlowInterval, poller.Step(fast: false));
        Assert.Equal(PadPoller.FastInterval, poller.Step(fast: true));
        Assert.Equal(asked + 2, api.Asked); // slot 2 only

        api.Pads[2] = new XInputState(0x0020 | 0x0010, 0, 0, 0, 0);
        poller.Step(false);
        Assert.Equal(GamepadButtons.View | GamepadButtons.Menu, readings[^1].Buttons);

        api.Pads.Clear(); // unplugged with buttons down
        Assert.Null(poller.Step(false));
        Assert.Equal(GamepadButtons.None, readings[^1].Buttons);
        var polls = poller.Polls;
        Assert.Null(poller.Step(false));
        Assert.Equal(polls, poller.Polls);

        api.Pads[0] = default; // an arrival: the reader rescans
        Assert.True(poller.Scan());
        Assert.NotNull(poller.Step(false));
    }

    [Fact]
    public void ThePollerMapsTriggersWithHysteresisAndTheLeftStick()
    {
        var api = new FakeXInput();
        api.Pads[0] = new XInputState(0, 110, 0, 0, 0);
        var poller = new PadPoller(api);
        List<PadReading> readings = [];
        poller.Reading += readings.Add;
        poller.Scan();

        poller.Step(false);
        api.Pads[0] = new XInputState(0, 80, 0, 0, 0); // under "on" but over "off": still held
        poller.Step(false);
        api.Pads[0] = new XInputState(0, 40, 0, 0, 0);
        poller.Step(false);
        api.Pads[0] = new XInputState(0, 0, 255, 32767, 0);
        poller.Step(false);

        Assert.Equal([GamepadButtons.LT, GamepadButtons.LT, GamepadButtons.None, GamepadButtons.RT], readings.Select(r => r.Buttons));
        Assert.True(readings[^1].StickX > 0.99f);
    }

    [Fact]
    public void ASlotThatReconnectsDoesNotInheritAHeldTrigger()
    {
        var api = new FakeXInput();
        api.Pads[0] = new XInputState(0, 200, 0, 0, 0);
        var poller = new PadPoller(api);
        PadReading last = default;
        poller.Reading += r => last = r;
        poller.Scan();
        poller.Step(false);
        Assert.Equal(GamepadButtons.LT, last.Buttons);

        api.Pads.Clear();
        poller.Scan(); // seen gone by a rescan (resume), not by a poll
        api.Pads[0] = new XInputState(0, 80, 0, 0, 0); // back, trigger between off and on
        poller.Scan();
        poller.Step(false);

        Assert.Equal(GamepadButtons.None, last.Buttons);
    }

    [Fact]
    public void ThePollerMergesPadsAndTakesTheStrongestStick()
    {
        var api = new FakeXInput();
        api.Pads[0] = new XInputState(0x1000, 0, 0, 3000, 0);
        api.Pads[1] = new XInputState(0x2000, 0, 0, 0, -30000);
        var poller = new PadPoller(api);
        PadReading last = default;
        poller.Reading += r => last = r;
        poller.Scan();

        poller.Step(false);

        Assert.Equal(GamepadButtons.A | GamepadButtons.B, last.Buttons);
        Assert.True(last.StickY < -0.9f);
    }

    // ---- The pipe ops -----------------------------------------------------------------------------------------

    private static EngineController Controller(IGamepadControl? gamepads) => new(() => ConfigSnapshot.Empty,
        new FlowLauncher(new FlowRunner([], TimeProvider.System), new RunHistory(null)), new RunHistory(null),
        EngineController.CreateDryRunner(TimeProvider.System), new Autostart(@"Software\DazeControl.Tests\x\Run", @"Software\DazeControl.Tests\x\Approved"),
        new EngineInfo("1.2.3", 42, @"C:\x\DazeControl.exe", "c.yaml"), _ => { }, gamepads: gamepads);

    [Fact]
    public async Task DevicesGamepadsListsAPadWithNoChordConfigured()
    {
        _backend.Pads = [new PadInfo(0, "wired"), new PadInfo(2, null)];
        using var service = Service();

        var answer = await Controller(service).HandleAsync(new PipeRequest { Id = 1, Op = Ops.Devices, Kind = "gamepads" }, TestContext.Current.CancellationToken);

        Assert.True(answer.Ok, answer.Error);
        Assert.Equal(["Controller 1", "Controller 3"], answer.Devices!.Select(d => d.Name));
        Assert.Equal("wired", answer.Devices![0].Detail);
        Assert.Equal("connected", answer.Devices![0].State);
        Assert.Equal(0, _backend.Starts); // listing never starts the reader
    }

    [Fact]
    public async Task RecordGamepadAnswersTheChordInTheEditorsFormat()
    {
        using var service = Service();
        var answer = Controller(service).HandleAsync(new PipeRequest { Id = 2, Op = Ops.RecordGamepad }, TestContext.Current.CancellationToken);
        _backend.Press(GamepadButtons.None);
        _backend.Press(GamepadButtons.View | GamepadButtons.Menu);
        _backend.Press(GamepadButtons.Menu);

        var message = await answer.WaitAsync(Wait, TestContext.Current.CancellationToken);

        Assert.True(message.Ok);
        Assert.Equal("View+Menu", message.Yaml);
        Assert.Equal(GamepadRecordStatus.Chord, Interpret(message));
    }

    [Fact]
    public async Task RecordGamepadErrorsSayNoControllerOrTimedOutAsTheEditorExpects()
    {
        _backend.Pads = [];
        using var service = Service();
        var none = await Controller(service).HandleAsync(new PipeRequest { Id = 3, Op = Ops.RecordGamepad }, TestContext.Current.CancellationToken);

        Assert.False(none.Ok);
        Assert.Equal(GamepadRecordStatus.NoController, Interpret(none));

        var late = PipeMessage.Fail(4, GamepadRecord.TimedOut);
        Assert.Equal(GamepadRecordStatus.TimedOut, Interpret(late));
    }

    [Fact]
    public async Task WithoutAGamepadSideTheOpsAnswerPlainErrors()
    {
        var controller = Controller(null);

        Assert.False((await controller.HandleAsync(new PipeRequest { Id = 5, Op = Ops.RecordGamepad }, TestContext.Current.CancellationToken)).Ok);
        Assert.False((await controller.HandleAsync(new PipeRequest { Id = 6, Op = Ops.Devices, Kind = "gamepads" }, TestContext.Current.CancellationToken)).Ok);
    }

    private enum GamepadRecordStatus { Chord, NoController, TimedOut, Other }

    /// <summary>The same reading of an answer as the editor's <c>EngineGamepadRecorder.Interpret</c> (DESIGN §3.6).</summary>
    private static GamepadRecordStatus Interpret(PipeMessage answer) =>
        answer is { Ok: true, Yaml: { Length: > 0 } } ? GamepadRecordStatus.Chord
        : (answer.Error ?? "").Contains("no controller", StringComparison.OrdinalIgnoreCase) ? GamepadRecordStatus.NoController
        : (answer.Error ?? "").Contains("timed out", StringComparison.OrdinalIgnoreCase) ? GamepadRecordStatus.TimedOut
        : GamepadRecordStatus.Other;
}
