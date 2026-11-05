using DazeControl.Core.Input;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Ipc;
using DazeControl.Runtime.Control;
using DazeControl.Runtime.Lifecycle;
using DazeControl.Runtime.Triggers;

namespace DazeControl.Runtime.Tests;

/// <summary>T12.6/D-086: the seams around the pad reader (DESIGN §3.4, §3.6), no hardware.</summary>
public sealed class GamepadServiceTests : IDisposable
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    private sealed class FakeBackend : IPadBackend
    {
        public int Starts, Halts;
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

    private GamepadService Service() => new(_backend);

    [Fact]
    public void NothingStartsUntilSomeoneListens()
    {
        using var service = Service();

        Assert.Equal(0, _backend.Starts);
        Assert.False(service.Running);
        Assert.Single(service.List()); // devices gamepads lists a pad with no menu and no reader
        Assert.Equal(0, _backend.Starts);
    }

    [Fact]
    public void TheFirstListenerStartsTheReaderAndTheLastOneStopsIt()
    {
        using var service = Service();
        var a = service.Listen(_ => { });
        var b = service.Listen(_ => { });

        Assert.Equal(1, _backend.Starts);

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

    // ---- PadPoller: the reader's logic, step by step -------------------------------------------------------

    [Fact]
    public void WithNoPadThePollerNeverPolls()
    {
        var api = new FakeXInput();
        var poller = new PadPoller(api);

        Assert.False(poller.Scan());
        var asked = api.Asked;

        Assert.Null(poller.Step());
        Assert.Null(poller.Step());
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

        Assert.Equal(PadPoller.FastInterval, poller.Step());
        Assert.Equal(PadPoller.FastInterval, poller.Step());
        Assert.Equal(asked + 2, api.Asked); // slot 2 only

        api.Pads[2] = new XInputState(0x0020 | 0x0010, 0, 0, 0, 0);
        poller.Step();
        Assert.Equal(GamepadButtons.View | GamepadButtons.Menu, readings[^1].Buttons);

        api.Pads.Clear(); // unplugged with buttons down
        Assert.Null(poller.Step());
        Assert.Equal(GamepadButtons.None, readings[^1].Buttons);
        var polls = poller.Polls;
        Assert.Null(poller.Step());
        Assert.Equal(polls, poller.Polls);

        api.Pads[0] = default; // an arrival: the reader rescans
        Assert.True(poller.Scan());
        Assert.NotNull(poller.Step());
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

        poller.Step();
        api.Pads[0] = new XInputState(0, 80, 0, 0, 0); // under "on" but over "off": still held
        poller.Step();
        api.Pads[0] = new XInputState(0, 40, 0, 0, 0);
        poller.Step();
        api.Pads[0] = new XInputState(0, 0, 255, 32767, 0);
        poller.Step();

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
        poller.Step();
        Assert.Equal(GamepadButtons.LT, last.Buttons);

        api.Pads.Clear();
        poller.Scan(); // seen gone by a rescan (resume), not by a poll
        api.Pads[0] = new XInputState(0, 80, 0, 0, 0); // back, trigger between off and on
        poller.Scan();
        poller.Step();

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

        poller.Step();

        Assert.Equal(GamepadButtons.A | GamepadButtons.B, last.Buttons);
        Assert.True(last.StickY < -0.9f);
    }

    // ---- The pipe ops -----------------------------------------------------------------------------------------

    private static EngineController Controller(IGamepadControl? gamepads) => new(() => ConfigSnapshot.Empty,
        new FlowLauncher(new FlowRunner([], TimeProvider.System), new RunHistory(null)), new RunHistory(null),
        EngineController.CreateDryRunner(TimeProvider.System), new Autostart(@"Software\DazeControl.Tests\x\Run", @"Software\DazeControl.Tests\x\Approved"),
        new EngineInfo("1.2.3", 42, @"C:\x\DazeControl.exe", "c.yaml"), _ => { }, gamepads: gamepads);

    [Fact]
    public async Task DevicesGamepadsListsAPadWithNoMenuOpen()
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
    public async Task WithoutAGamepadSideTheOpsAnswerPlainErrors()
    {
        var controller = Controller(null);

        Assert.False((await controller.HandleAsync(new PipeRequest { Id = 6, Op = Ops.Devices, Kind = "gamepads" }, TestContext.Current.CancellationToken)).Ok);
    }
}
