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
    public async Task ADesktopRunRecordsItsTrigger()
    {
        var answer = await _controller.HandleAsync(new PipeRequest
        {
            Id = 7, Op = Ops.Run, Flow = "tv", Wait = true, ExactId = true, Trigger = EngineController.DesktopTrigger,
        }, CancellationToken.None);

        Assert.True(answer.Ok);
        Assert.Equal("Desktop shortcut", answer.Run!.Trigger);
    }

    [Fact]
    public async Task ADesktopRunNeverFallsBackToAName()
    {
        // A deleted flow's id may be another flow's name: the shortcut must not run that one.
        var refused = new List<RunRefusal>();
        _controller.RunRefused += refused.Add;

        var answer = await _controller.HandleAsync(new PipeRequest { Id = 7, Op = Ops.Run, Flow = "TV only", ExactId = true },
            CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.Empty(_history.Recent);
        var refusal = Assert.Single(refused);
        Assert.Equal(EngineController.ShortcutFlowGone, refusal.Message);
        Assert.Equal("Desktop shortcut", refusal.Title);
        Assert.True((await Send(Ops.Run, "TV only", wait: true)).Ok); // dctl keeps its name fallback
    }

    [Theory]
    [InlineData("broken", "Broken needs fixing before it can run.")]
    [InlineData("off", "Off is turned off.")]
    public async Task ADesktopRunThatCantStartTellsThePopup(string flow, string message)
    {
        var refused = new List<RunRefusal>();
        _controller.RunRefused += refused.Add;

        await _controller.HandleAsync(new PipeRequest { Id = 7, Op = Ops.Run, Flow = flow, ExactId = true }, CancellationToken.None);

        Assert.Equal(message, Assert.Single(refused).Message);
    }

    [Fact]
    public async Task ADesktopRunForAnotherConfigIsRefused()
    {
        // One engine per user, whatever its config: a shortcut made by another config's engine must not run "tv" here.
        var refused = new List<RunRefusal>();
        _controller.RunRefused += refused.Add;

        var answer = await _controller.HandleAsync(new PipeRequest
        {
            Id = 7, Op = Ops.Run, Flow = "tv", ExactId = true, ConfigPath = @"D:\other.yaml",
        }, CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.Empty(_history.Recent);
        Assert.Contains(@"D:\other.yaml", Assert.Single(refused).Message, StringComparison.Ordinal);
        Assert.True((await _controller.HandleAsync(new PipeRequest
        {
            Id = 8, Op = Ops.Run, Flow = "tv", ExactId = true, Wait = true, ConfigPath = Path.GetFullPath("c.yaml"),
        }, CancellationToken.None)).Ok);
    }

    [Fact]
    public async Task ACommandLineRunThatCantStartIsNoPopup()
    {
        var refused = new List<RunRefusal>();
        _controller.RunRefused += refused.Add;

        await Send(Ops.Run, "nope");
        await Send(Ops.Run, "off");

        Assert.Empty(refused);
    }

    [Fact]
    public async Task EditorShortcutOpsAreIdOnlyAndForThisConfigOnly()
    {
        var root = Path.Combine(Path.GetTempPath(), "dc-ctl-" + Guid.NewGuid().ToString("N"));
        var desktop = Path.Combine(root, "Desktop");
        Directory.CreateDirectory(desktop);
        try
        {
            var shortcuts = new DesktopShortcuts(new ShortcutPlaces(desktop, Path.Combine(root, "icons"), @"C:\x\DazeControl.exe",
                [@"C:\x\DazeControl.exe"], null, Path.GetFullPath("c.yaml")), new MemoryLinks(), new NoIcons());
            var controller = new EngineController(ReadConfig, _launcher, _history, EngineController.CreateDryRunner(_time),
                new Autostart(_root + @"\Run", _root + @"\Approved"), new EngineInfo("1.2.3", 42, @"C:\x\DazeControl.exe", "c.yaml"),
                _ => { }, shortcuts: shortcuts);
            Task<PipeMessage> Op(string op, string flow, bool exact, string? config = null) =>
                controller.HandleAsync(new PipeRequest { Id = 1, Op = op, Flow = flow, ExactId = exact, ConfigPath = config }, CancellationToken.None);

            Assert.False((await Op(Ops.CreateShortcut, "TV only", exact: true)).Ok); // a name, not an id
            Assert.True((await Op(Ops.CreateShortcut, "TV only", exact: false)).Ok); // dctl keeps the name
            Assert.Empty((await Op(Ops.RemoveShortcut, "TV only", exact: true)).Shortcuts!); // gone id: nothing else removed
            Assert.Single((await Op(Ops.Shortcuts, "", exact: false)).Shortcuts!);
            Assert.False((await Op(Ops.Shortcuts, "", exact: false, config: @"D:\other.yaml")).Ok);
            Assert.False((await Op(Ops.RemoveShortcut, "tv", exact: true, config: @"D:\other.yaml")).Ok);
            Assert.Single((await Op(Ops.RemoveShortcut, "tv", exact: true, config: Path.GetFullPath("c.yaml"))).Shortcuts!);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MemoryLinks : IShellLinkStore
    {
        private readonly Dictionary<string, ShellLinkData> _files = new(StringComparer.OrdinalIgnoreCase);

        public IEnumerable<string> Links(string directory, bool recursive = false) => Directory.EnumerateFiles(directory, "*.lnk");

        public ShellLinkData? Read(string path) => _files.GetValueOrDefault(path);

        public void Write(string path, ShellLinkData link)
        {
            File.WriteAllText(path, "");
            _files[path] = link;
        }

        public void Delete(string path)
        {
            File.Delete(path);
            _files.Remove(path);
        }
    }

    private sealed class NoIcons : IFlowIconRenderer
    {
        public void Write(string path, string glyph) => throw new IOException("no icons in tests");
    }

    [Fact]
    public async Task ShortcutOpsWithoutTheShellSayWhy()
    {
        var answer = await Send(Ops.Shortcuts);

        Assert.Equal("This engine can't make desktop shortcuts.", answer.Error);
    }

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

    private const string TwoYaml = """
        flows:
          two:
            name: Two steps
            nodes:
              - { id: first, step: notify, with: { text: x } }
              - { id: second, step: notify, with: { text: y }, after: [first] }
        """;

    [Fact]
    public async Task RunFromAStepRunsTheSavedFlowFromThereAndRecordsIt()
    {
        _snapshot = ConfigHost.Apply(ConfigSnapshot.Empty, ConfigYaml.Load(TwoYaml));
        // T27.4, D-148: a live run through the launcher; the first step counts as done without running.
        var answer = await _controller.HandleAsync(
            new PipeRequest { Id = 7, Op = Ops.Run, Flow = "two", Node = "second", Wait = true, Trigger = "Editor" }, CancellationToken.None);

        Assert.True(answer.Ok, answer.Error);
        Assert.Equal(["second"], _provider.Attempts.Select(a => a.Step));
        var record = Assert.Single(_history.Recent);
        Assert.Equal("Editor (from Show message)", record.Trigger);
        Assert.Equal(FlowOutcome.Succeeded, record.Outcome);
        var first = record.Nodes.Single(n => n.NodeId == "first");
        Assert.Equal((0, "this run started at Show message"), (first.Attempts, first.SkippedBecause));
    }

    [Fact]
    public async Task StopCancelsARunFromAStep()
    {
        var run = _controller.HandleAsync(new PipeRequest { Id = 7, Op = Ops.Run, Flow = "slow", Node = "a", Wait = true }, CancellationToken.None);
        await _provider.Started("a");

        Assert.True((await Send(Ops.Cancel, "slow")).Ok);

        Assert.Equal(FlowOutcome.Cancelled, (await run).Run!.Outcome);
    }

    [Fact]
    public async Task RunFromAStepOnlyInTheDraftIsRefused()
    {
        _snapshot = ConfigHost.Apply(ConfigSnapshot.Empty, ConfigYaml.Load(TwoYaml));
        var answer = await _controller.HandleAsync(new PipeRequest { Id = 7, Op = Ops.Run, Flow = "two", Node = "draft" }, CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.Equal("That step isn't in the saved Two steps. Save the flow, then try again.", answer.Error);
        Assert.Empty(_history.Recent);
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
    public async Task FlagReadsAndSetsByNameAndPauseGoesThroughTheHost()
    {
        var flags = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var paused = false;
        _controller.FlagAccess = (name, value) =>
        {
            if (value is { } set)
                flags[name] = set;
            return flags.GetValueOrDefault(name);
        };
        _controller.PauseAccess = value =>
        {
            paused = value ?? paused;
            return Task.FromResult(paused);
        };
        Task<PipeMessage> Flag(string? name, bool? enable) =>
            _controller.HandleAsync(new PipeRequest { Id = 1, Op = Ops.Flag, Key = name, Enable = enable }, CancellationToken.None);
        Task<PipeMessage> Pause(bool? enable) =>
            _controller.HandleAsync(new PipeRequest { Id = 1, Op = Ops.Pause, Enable = enable }, CancellationToken.None);

        Assert.False((await Flag(" gaming ", null)).State);
        Assert.True((await Flag(" gaming ", true)).State);
        Assert.True((await Flag("GAMING", null)).State); // names ignore case, trimmed
        Assert.False((await Flag("gaming", false)).State);
        var noName = await Flag(" ", true);
        Assert.False(noName.Ok);
        Assert.True(noName.Usage);
        Assert.True((await Flag(new string('x', EngineController.MaxFlagName + 1), true)).Usage);

        Assert.False((await Pause(null)).State);
        Assert.True((await Pause(true)).State);
        Assert.True((await Pause(null)).State);
        Assert.False((await Pause(false)).State);
    }

    [Fact]
    public async Task FlagAndPauseSayWhenTheHostGaveNoAccess()
    {
        var flag = await _controller.HandleAsync(new PipeRequest { Id = 1, Op = Ops.Flag, Key = "a" }, CancellationToken.None);
        var pause = await _controller.HandleAsync(new PipeRequest { Id = 1, Op = Ops.Pause }, CancellationToken.None);

        Assert.False(flag.Ok);
        Assert.False(pause.Ok);
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
    public async Task AndroidAppsAsksTheDeviceListOnlyAsTold()
    {
        var devices = new AppDevices();
        var controller = new EngineController(ReadConfig, _launcher, _history, EngineController.CreateDryRunner(_time),
            new Autostart(_root + @"\Run", _root + @"\Approved"), new EngineInfo("1.2.3", 42, "x.exe", "c.yaml"), _ => { }, devices);

        var saved = await controller.HandleAsync(new PipeRequest { Id = 1, Op = Ops.AndroidApps, Address = " tv.lan " }, CancellationToken.None);
        var fresh = await controller.HandleAsync(new PipeRequest { Id = 2, Op = Ops.AndroidApps, Address = "tv.lan", Adb = "adb2", Refresh = true }, CancellationToken.None);
        var none = await controller.HandleAsync(new PipeRequest { Id = 3, Op = Ops.AndroidApps }, CancellationToken.None);

        Assert.Equal(["tv.lan|False", "tv.lan|True|adb2"], devices.Calls);
        Assert.Equal("Kodi", Assert.Single(saved.Devices!).Name);
        Assert.True(fresh.Ok);
        Assert.False(none.Ok);
    }

    private sealed class AppDevices : IDeviceQueries
    {
        public List<string> Calls { get; } = [];

        public IReadOnlyList<string> Kinds { get; } = [];

        public Task<IReadOnlyList<DeviceInfo>> ListAsync(string kind, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<DisplayLayout> CaptureDisplayLayoutAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<DeviceInfo>> AndroidAppsAsync(string address, string? adbPath, bool refresh, CancellationToken cancellationToken)
        {
            Calls.Add($"{address}|{refresh}" + (adbPath is null ? "" : "|" + adbPath));
            return Task.FromResult<IReadOnlyList<DeviceInfo>>([new DeviceInfo { Name = "Kodi", Value = "org.xbmc.kodi" }]);
        }
    }

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
              - { id: c, step: notify, with: { text: x }, after: [a], timeout: nonsense }
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

    [Fact]
    public async Task TestStepKeepsTheFlowsInputs()
    {
        const string yaml = """
            library:
              audio: { technics: { match: Technics } }
            flows:
              mk:
                inputs: { output: { kind: audio, default: technics } }
                nodes:
                  - { id: set, step: audio.setDefault, with: { device: $output } }
            """;

        var answer = await WithStepRunner().HandleAsync(Test("set", "mk", yaml), CancellationToken.None);

        Assert.True(answer.Ok, answer.Error); // validated with its input; it runs (and fails here: no audio provider)
        Assert.NotNull(answer.Run);
    }

    [Theory]
    [InlineData("b", "Fix this step first:")] // notify without text
    [InlineData("zz", "That step isn't in the flow.")]
    [InlineData("c", "Fix this step first:")] // a timeout mapping replaced with the default
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

    private const string DryYaml = """
        library:
          bluetooth:
            technics: { address: "B8:20:8E:00:00:01" }
        flows:
          tv:
            name: TV only
            nodes:
              - { id: here, step: bluetooth.connect, with: { device: technics } }
              - { id: gone, step: bluetooth.connect, with: { device: buds }, onFailure: skip }
              - { id: after, step: notify, with: { text: x }, after: [gone] }
        """;

    [Fact]
    public async Task DryRunOfTheSentConfigFailsMissingDevicesAndSkipsWhatWaits()
    {
        var answer = await _controller.HandleAsync(
            new PipeRequest { Id = 3, Op = Ops.Run, Flow = "tv", DryRun = true, Config = DryYaml, Trigger = "Editor" }, CancellationToken.None);

        Assert.True(answer.Ok, answer.Error);
        var nodes = answer.Run!.Nodes.ToDictionary(n => n.NodeId);
        Assert.Equal(NodeOutcome.Succeeded, nodes["here"].Outcome);
        Assert.Equal(NodeOutcome.Failed, nodes["gone"].Outcome);
        Assert.Equal("There's no Bluetooth device called \"buds\" in your Library.", nodes["gone"].Error);
        Assert.Equal(NodeOutcome.Skipped, nodes["after"].Outcome);
        Assert.Equal("Editor (dry run)", answer.Run.Trigger);
        Assert.Empty(_history.Recent);
    }

    [Fact]
    public async Task DryRunFromAStepWalksOnlyThatStepAndWhatFollows()
    {
        var answer = await _controller.HandleAsync(
            new PipeRequest { Id = 3, Op = Ops.Run, Flow = "tv", DryRun = true, Config = DryYaml, Trigger = "Editor", Node = "after" }, CancellationToken.None);

        Assert.True(answer.Ok, answer.Error);
        var nodes = answer.Run!.Nodes.ToDictionary(n => n.NodeId);
        Assert.Equal(0, nodes["here"].Order);
        Assert.Equal(0, nodes["gone"].Order);
        Assert.NotNull(nodes["gone"].SkippedBecause);
        Assert.Equal(NodeOutcome.Succeeded, nodes["after"].Outcome);
        Assert.Equal(1, nodes["after"].Order);
        Assert.Equal("Editor (from Show message) (dry run)", answer.Run.Trigger);
    }

    [Fact]
    public async Task RunFromAStepTheFlowDoesntHaveIsRefused()
    {
        var answer = await _controller.HandleAsync(
            new PipeRequest { Id = 3, Op = Ops.Run, Flow = "tv", DryRun = true, Config = DryYaml, Node = "nope" }, CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.Equal("That step isn't in the flow.", answer.Error);
    }

    [Fact]
    public async Task DryRunOfTheSentConfigRefusesAFlowThatCantBeWalked()
    {
        var loop = DryYaml.Replace("onFailure: skip }", "onFailure: skip, after: [after] }", StringComparison.Ordinal);

        var answer = await _controller.HandleAsync(
            new PipeRequest { Id = 3, Op = Ops.Run, Flow = "tv", DryRun = true, Config = loop }, CancellationToken.None);

        Assert.False(answer.Ok);
        Assert.StartsWith("Fix this first:", answer.Error, StringComparison.Ordinal);
    }

    /// <summary>The user's live Connect Technics flow (2026-11-03), bt on "Keep going" with a fallback.</summary>
    private const string TechnicsYaml = """
        library:
          audio: { technics: { match: "Technics" }, speakers: { match: "Realtek" } }
          bluetooth: { technics: { address: "B8:20:8E:00:00:01" }, sony: { address: "B8:20:8E:00:00:02" } }
        flows:
          connect-technics:
            name: Connect Technics
            nodes:
              - { id: bt, step: bluetooth.connect, with: { device: technics }, onFailure: ignore, timeout: 10s }
              - { id: out, step: audio.setDefault, with: { device: technics }, after: [bt] }
              - { id: connect, step: bluetooth.connect, with: { device: sony }, after: [{ id: bt, when: failure }] }
              - { id: setDefault, step: audio.setDefault, with: { device: speakers }, after: [connect] }
        """;

    private async Task<RunRecord> DryRun(string yaml)
    {
        var answer = await _controller.HandleAsync(
            new PipeRequest { Id = 3, Op = Ops.Run, Flow = "connect-technics", DryRun = true, Config = yaml }, CancellationToken.None);
        Assert.True(answer.Ok, answer.Error);
        return answer.Run!;
    }

    [Fact]
    public async Task DryRunOfConnectTechnicsIsDoneAndTheSonyBranchNotNeeded()
    {
        // Before D-065 this said "done, with a problem" every time the headphones simply worked.
        var run = await DryRun(TechnicsYaml);

        Assert.Equal(FlowOutcome.Succeeded, run.Outcome);
        Assert.Equal([NodeOutcome.Succeeded, NodeOutcome.Succeeded, NodeOutcome.NotNeeded, NodeOutcome.NotNeeded],
            run.Nodes.Select(n => n.Outcome));
    }

    [Theory]
    [InlineData("ignore", FlowOutcome.Failed)]
    [InlineData("skip", FlowOutcome.Succeeded)]
    public async Task DryRunOfConnectTechnicsWithoutTechnics(string policy, FlowOutcome expected)
    {
        // With Technics gone, "Keep going" also runs out, which fails and stops the flow; "Skip what depends on it"
        // leaves out to the fallback and the run is Done.
        var yaml = TechnicsYaml.Replace("technics: { address: \"B8:20:8E:00:00:01\" }, ", "", StringComparison.Ordinal)
            .Replace("technics: { match: \"Technics\" }, ", "", StringComparison.Ordinal)
            .Replace("onFailure: ignore", "onFailure: " + policy, StringComparison.Ordinal);

        var run = await DryRun(yaml);

        Assert.Equal(expected, run.Outcome);
    }

    [Fact]
    public async Task DryRunDoesNotWaitOutRetriesOfAMissingDevice()
    {
        var yaml = DryYaml.Replace("onFailure: skip }", "onFailure: skip, retry: { count: 3, delay: 30s } }", StringComparison.Ordinal);

        // ManualTime never advances here: a retry delay would hang the request.
        var answer = await _controller.HandleAsync(
            new PipeRequest { Id = 3, Op = Ops.Run, Flow = "tv", DryRun = true, Config = yaml }, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(1, answer.Run!.Nodes.Single(n => n.NodeId == "gone").Attempts);
    }

    private Task<PipeMessage> RecordHotkey(bool on, CancellationToken connection) =>
        _controller.HandleAsync(new PipeRequest { Id = 7, Op = Ops.RecordHotkey, On = on }, connection);

    [Fact]
    public async Task RecordHotkeyTurnsRecordingOnAndOff()
    {
        var seen = new List<bool>();
        _controller.HotkeyRecordingChanged += seen.Add;
        using var connection = new CancellationTokenSource();

        Assert.True((await RecordHotkey(true, connection.Token)).Ok);
        Assert.True((await RecordHotkey(true, connection.Token)).Ok); // focus came again: no second "on"
        Assert.True((await RecordHotkey(false, connection.Token)).Ok);
        Assert.True((await RecordHotkey(false, connection.Token)).Ok);

        Assert.Equal([true, false], seen);
    }

    [Fact]
    public async Task ADroppedConnectionEndsRecording()
    {
        var seen = new List<bool>();
        _controller.HotkeyRecordingChanged += seen.Add;
        using var connection = new CancellationTokenSource();
        await RecordHotkey(true, connection.Token);

        await connection.CancelAsync();

        Assert.Equal([true, false], seen);
    }

    [Fact]
    public async Task RecordingEndsWhenTheLastRecordingConnectionStops()
    {
        var seen = new List<bool>();
        _controller.HotkeyRecordingChanged += seen.Add;
        using var one = new CancellationTokenSource();
        using var two = new CancellationTokenSource();
        await RecordHotkey(true, one.Token);
        await RecordHotkey(true, two.Token);

        await RecordHotkey(false, one.Token);
        Assert.Equal([true], seen);

        await two.CancelAsync();
        Assert.Equal([true, false], seen);
    }

    [Fact]
    public async Task RecordHotkeyWithoutOnIsRefused()
    {
        var answer = await _controller.HandleAsync(new PipeRequest { Id = 7, Op = Ops.RecordHotkey }, CancellationToken.None);

        Assert.False(answer.Ok);
    }
}
