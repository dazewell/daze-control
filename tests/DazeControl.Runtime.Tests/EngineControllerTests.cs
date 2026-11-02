using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Yaml;
using DazeControl.Ipc;
using DazeControl.Runtime.Control;
using DazeControl.Runtime.Lifecycle;
using DazeControl.Runtime.Providers;

namespace DazeControl.Runtime.Tests;

public sealed class EngineControllerTests : IDisposable
{
    private const string Yaml = """
        flows:
          tv:
            name: TV only
            hotkeys: [Alt+T]
            nodes: [{ id: a, step: notify, with: { text: x } }]
          slow:
            name: Slow one
            nodes: [{ id: a, step: notify, with: { text: x } }]
          broken:
            name: Broken
            nodes: [{ id: a, step: nope }]
          off:
            name: Off
            enabled: false
            nodes: [{ id: a, step: notify, with: { text: x } }]
        """;

    private readonly string _root = @"Software\DazeControl.Tests\" + Guid.NewGuid().ToString("N");
    private readonly ManualTime _time = new();
    private readonly FakeProvider _provider = new();
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly FlowLauncher _launcher;
    private readonly RunHistory _history = new(null);
    private readonly EngineController _controller;
    private readonly List<string> _log = [];
    private ConfigSnapshot _snapshot;

    /// <summary>When set, the config changes to this right after the next read (a reload mid-request).</summary>
    private ConfigSnapshot? _afterNextRead;

    public EngineControllerTests()
    {
        _provider.On("a", (context, token) => context.Flow.Id is "slow" or "test:slow" ? _gate.Task.WaitAsync(token) : Task.CompletedTask);
        _launcher = new FlowLauncher(new FlowRunner([_provider], _time), _history);
        _snapshot = ConfigHost.Apply(ConfigSnapshot.Empty, ConfigYaml.Load(Yaml));
        _controller = new EngineController(ReadConfig, _launcher, _history, EngineController.CreateDryRunner(_time),
            new Autostart(_root + @"\Run", _root + @"\Approved"), new EngineInfo("1.2.3", 42, @"C:\x\DazeControl.exe", "c.yaml"),
            line => { lock (_log) _log.Add(line); });
    }

    private ConfigSnapshot ReadConfig()
    {
        var current = _snapshot;
        if (_afterNextRead is { } next)
            (_snapshot, _afterNextRead) = (next, null);
        return current;
    }

    public void Dispose()
    {
        _gate.TrySetResult();
        _time.Dispose();
        Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(_root, throwOnMissingSubKey: false);
    }

    private Task<PipeMessage> Send(string op, string? flow = null, bool wait = false, bool dryRun = false, bool? enable = null, int? limit = null) =>
        _controller.HandleAsync(new PipeRequest { Id = 7, Op = op, Flow = flow, Wait = wait, DryRun = dryRun, Enable = enable, Limit = limit },
            CancellationToken.None);

    [Fact]
    public async Task RunWithWaitAnswersWithTheRecord()
    {
        var answer = await Send(Ops.Run, "tv", wait: true);

        Assert.True(answer.Ok);
        Assert.Equal(7, answer.Id);
        Assert.Equal(FlowOutcome.Succeeded, answer.Run!.Outcome);
        Assert.Equal("Command line", answer.Run.Trigger);
        Assert.Single(_history.Recent);
    }

    [Fact]
    public async Task AFlowIsFoundByItsNameToo()
    {
        Assert.True((await Send(Ops.Run, "tv ONLY", wait: true)).Ok);
    }

    [Theory]
    [InlineData("nope", "There's no flow called \"nope\".")]
    [InlineData(null, "Say which flow.")]
    [InlineData("broken", "Broken needs fixing before it can run.")]
    [InlineData("off", "Off is turned off.")]
    public async Task RunRefusesWhatCantRun(string? flow, string error)
    {
        var answer = await Send(Ops.Run, flow);

        Assert.False(answer.Ok);
        Assert.Equal(error, answer.Error);
    }

    [Fact]
    public async Task RunWithoutWaitAnswersAtOnceAndAnIgnoredSecondStartSaysSo()
    {
        Assert.True((await Send(Ops.Run, "slow")).Ok);
        await _provider.Started("a");

        var second = await Send(Ops.Run, "slow");

        Assert.Equal("Slow one is already running.", second.Error);
        Assert.Equal(["slow"], (await Send(Ops.Status)).Status!.Running!);
    }

    [Fact]
    public async Task CancelStopsARunningFlow()
    {
        var run = Send(Ops.Run, "slow", wait: true);
        await _provider.Started("a");

        Assert.True((await Send(Ops.Cancel, "slow")).Ok);

        Assert.Equal(FlowOutcome.Cancelled, (await run).Run!.Outcome);
        Assert.Equal("Slow one isn't running.", (await Send(Ops.Cancel, "slow")).Error);
    }

    [Fact]
    public async Task DryRunWalksTheFlowWithoutRunningStepsOrHistory()
    {
        var answer = await Send(Ops.Run, "tv", dryRun: true);

        Assert.Equal(FlowOutcome.Succeeded, answer.Run!.Outcome);
        Assert.Empty(_provider.Attempts);
        Assert.Empty(_history.Recent);
    }

    [Fact]
    public async Task ListDescribesEveryFlow()
    {
        var flows = (await Send(Ops.List)).Flows!;

        Assert.Equal(["tv", "slow", "broken", "off"], flows.Select(f => f.Id));
        Assert.Equal(["Alt+T"], flows[0].Hotkeys!);
        Assert.True(flows[0].Runnable);
        Assert.False(flows[2].Runnable);
        Assert.Equal("Broken", flows[2].Name);
        Assert.Equal(1, flows[2].Errors);
        Assert.False(flows[3].Runnable);
    }

    [Fact]
    public async Task StatusCarriesTheShellState()
    {
        _controller.Shell = new ShellStatus(true, ["Alt+T: taken"]);

        var status = (await Send(Ops.Status)).Status!;

        Assert.Equal("1.2.3", status.Version);
        Assert.True(status.ShortcutsPaused);
        Assert.Equal(["Alt+T: taken"], status.ShortcutProblems!);
        Assert.Equal(1, status.Errors);
    }

    [Fact]
    public async Task ValidateListsTheConfigsProblems()
    {
        var problem = Assert.Single((await Send(Ops.Validate)).Problems!);

        Assert.Equal("error", problem.Severity);
        Assert.Equal("broken", problem.FlowId);
    }

    [Fact]
    public async Task HistoryIsNewestFirstAndFiltersByFlow()
    {
        await Send(Ops.Run, "tv", wait: true);
        _time.Advance(TimeSpan.FromSeconds(1));
        await Send(Ops.Run, "tv", wait: true);

        var all = (await Send(Ops.History)).History!;
        Assert.Equal(2, all.Length);
        Assert.True(all[0].Started > all[1].Started);
        Assert.Single((await Send(Ops.History, "tv", limit: 1)).History!);
        Assert.Empty((await Send(Ops.History, "slow")).History!);
    }

    [Fact]
    public async Task AutostartPointsAtTheEngine()
    {
        Assert.Equal("off", (await Send(Ops.Autostart)).Autostart!.State);

        var on = (await Send(Ops.Autostart, enable: true)).Autostart!;
        Assert.Equal("on", on.State);
        Assert.Equal(@"C:\x\DazeControl.exe", on.Path);

        Assert.Equal("off", (await Send(Ops.Autostart, enable: false)).Autostart!.State);
    }

    [Theory]
    [InlineData(Ops.Devices)]
    [InlineData(Ops.CaptureDisplayLayout)]
    [InlineData(Ops.TestStep)]
    [InlineData("frobnicate")]
    public async Task UnsupportedOpsAnswerWithAnError(string op)
    {
        var answer = await Send(op);

        Assert.False(answer.Ok);
        Assert.NotNull(answer.Error);
    }

    [Fact]
    public async Task TriggerLogsWhatCantRun()
    {
        Assert.Null(await _controller.TriggerAsync("broken", "Tray"));

        Assert.Contains("run: broken (Tray) not runnable", _log);
    }

    [Fact]
    public async Task ARunOutlivesItsFlowForCancelAndStatus()
    {
        var run = Send(Ops.Run, "slow", wait: true);
        await _provider.Started("a");
        _snapshot = ConfigSnapshot.Empty;

        Assert.Equal(["slow"], (await Send(Ops.Status)).Status!.Running!);
        Assert.True((await Send(Ops.Cancel, "slow")).Ok);
        Assert.Equal(FlowOutcome.Cancelled, (await run).Run!.Outcome);
    }

    [Fact]
    public async Task RunLaunchesTheVersionItChecked()
    {
        _afterNextRead = ConfigSnapshot.Empty;

        var answer = await Send(Ops.Run, "tv", wait: true);

        Assert.True(answer.Ok);
        Assert.Equal("TV only", answer.Run!.FlowName);
        Assert.Single(_history.Recent);
    }

    [Fact]
    public async Task HistoryOfARemovedFlowIsFoundByIdOrName()
    {
        await Send(Ops.Run, "tv", wait: true);
        _snapshot = ConfigSnapshot.Empty;

        Assert.Single((await Send(Ops.History, "tv")).History!);
        Assert.Single((await Send(Ops.History, "tv only")).History!);
        Assert.Equal("There's no flow called \"nope\".", (await Send(Ops.History, "nope")).Error);
    }

    /// <summary>A well-formed capture of two screens: [2][0], then two zeroed paths.</summary>
    private const string TwoScreens = "AgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private sealed class FakeDevices : IDeviceQueries
    {
        public IReadOnlyList<string> Kinds { get; } = ["audio", "displays"];

        public Task<IReadOnlyList<DeviceInfo>> ListAsync(string kind, CancellationToken cancellationToken) => kind == "audio"
            ? Task.FromResult<IReadOnlyList<DeviceInfo>>([new DeviceInfo { Name = "Speakers", Detail = "Output, default" }])
            : throw new StepFailedException("Can't read the screens.");

        public Task<DisplayLayout> CaptureDisplayLayoutAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(new DisplayLayout(key, [@"\?\DISPLAY#DEL1#1", "TV"], TwoScreens));
    }

    private EngineController WithDevices() => new(ReadConfig, _launcher, _history, EngineController.CreateDryRunner(_time),
        new Autostart(_root + @"\Run", _root + @"\Approved"), new EngineInfo("1.2.3", 42, "x.exe", "c.yaml"), _ => { }, new FakeDevices());

    [Fact]
    public async Task DevicesListsTheKindAsked()
    {
        var answer = await WithDevices().HandleAsync(new PipeRequest { Id = 3, Op = Ops.Devices, Kind = "audio" }, CancellationToken.None);

        Assert.True(answer.Ok);
        Assert.Equal("Speakers", Assert.Single(answer.Devices!).Name);
    }

    [Theory]
    [InlineData(null, "Say which devices: audio, displays.")]
    [InlineData("toaster", "Say which devices: audio, displays.")]
    [InlineData("displays", "Can't read the screens.")]
    public async Task DevicesExplainsWhatWentWrong(string? kind, string error)
    {
        var answer = await WithDevices().HandleAsync(new PipeRequest { Id = 3, Op = Ops.Devices, Kind = kind }, CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.Equal(error, answer.Error);
    }

    [Fact]
    public async Task CapturedLayoutIsASnippetThatLoadsBack()
    {
        var answer = await WithDevices().HandleAsync(new PipeRequest { Id = 3, Op = Ops.CaptureDisplayLayout, Key = "desk" }, CancellationToken.None);

        Assert.True(answer.Ok, answer.Error);
        var load = ConfigYaml.Load(answer.Yaml!);
        Assert.Empty(load.Problems);
        var layout = Assert.Single(load.Config!.Library.Displays);
        Assert.Equal(new DisplayLayout("desk", [@"\?\DISPLAY#DEL1#1", "TV"], TwoScreens), layout);
    }

    [Fact]
    public async Task CaptureNeedsAName()
    {
        var answer = await WithDevices().HandleAsync(new PipeRequest { Id = 3, Op = Ops.CaptureDisplayLayout, Key = " " }, CancellationToken.None);

        Assert.Equal("Say what to call the layout.", answer.Error);
    }

    private const string TestYaml = """
        flows:
          tv:
            name: TV only
            nodes:
              - { id: a, step: notify, with: { text: x } }
              - { id: b, step: notify, after: [a] }
        """;

    private EngineController WithStepRunner() => new(ReadConfig, _launcher, _history, EngineController.CreateDryRunner(_time),
        new Autostart(_root + @"\Run", _root + @"\Approved"), new EngineInfo("1.2.3", 42, @"C:\x\DazeControl.exe", "c.yaml"),
        _ => { }, stepRunner: new FlowRunner([_provider], _time));

    private static PipeRequest Test(string node, string flow = "tv", string config = TestYaml) =>
        new() { Id = 5, Op = Ops.TestStep, Flow = flow, Node = node, Config = config };

    [Fact]
    public async Task TestStepRunsTheSentStepAloneWithoutHistory()
    {
        var answer = await WithStepRunner().HandleAsync(Test("a"), CancellationToken.None);

        Assert.True(answer.Ok, answer.Error);
        Assert.Equal(FlowOutcome.Succeeded, answer.Run!.Outcome);
        Assert.Equal("test:tv", answer.Run.FlowId);
        Assert.Equal(["a"], answer.Run.Nodes.Select(n => n.NodeId));
        Assert.Equal([("a", 1)], _provider.Attempts);
        Assert.Empty(_history.Recent);
    }

    [Theory]
    [InlineData("b", "Fix this step first:")] // notify without text
    [InlineData("zz", "That step isn't in the flow.")]
    public async Task TestStepRefusesWhatCantRun(string node, string error)
    {
        var answer = await WithStepRunner().HandleAsync(Test(node), CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.StartsWith(error, answer.Error, StringComparison.Ordinal);
        Assert.Empty(_provider.Attempts);
    }

    [Fact]
    public async Task CancelStopsAStepTest()
    {
        var controller = WithStepRunner();
        var test = controller.HandleAsync(Test("a", "slow", Yaml), CancellationToken.None);
        await _provider.Started("a");

        Assert.True((await controller.HandleAsync(new PipeRequest { Id = 6, Op = Ops.Cancel, Flow = "test:slow" }, CancellationToken.None)).Ok);

        Assert.Equal(FlowOutcome.Cancelled, (await test).Run!.Outcome);
    }
}
