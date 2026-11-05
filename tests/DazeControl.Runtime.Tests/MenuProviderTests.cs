using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Yaml;
using DazeControl.Ipc;
using DazeControl.Runtime.Control;
using DazeControl.Runtime.Lifecycle;
using DazeControl.Runtime.Menus;
using DazeControl.Runtime.Popup;
using DazeControl.Runtime.Providers;
using static DazeControl.Runtime.Tests.FlowRunnerTests;

namespace DazeControl.Runtime.Tests;

/// <summary><c>menu.show</c> at run time (M12, DESIGN §2.2, §3.2), with a fake presenter: no window, no pad.</summary>
public sealed class MenuProviderTests : IDisposable
{
    private static readonly PopupSettings Settings = new(PopupPosition.BottomCenter, 3);

    private readonly ManualTime _time = new();
    private readonly FakeProvider _provider = new();
    private readonly Dictionary<string, Flow> _flows = [];
    private readonly List<RunEvent> _events = [];
    private readonly Lock _lock = new();
    private readonly Starter _starter;
    private readonly FakePresenter _presenter = new();
    private readonly FlowRunner _runner;

    public MenuProviderTests()
    {
        _starter = new Starter(this);
        _runner = new FlowRunner([_provider, new MenuProvider(new GatedMenuPresenter(_presenter), _starter)], _time, events: e =>
        {
            lock (_lock)
                _events.Add(e);
        })
        { Flows = _starter };
    }

    public void Dispose() => _time.Dispose();

    private void Define(string yaml)
    {
        var load = ConfigYaml.Load("flows:\n" + yaml);
        Assert.Empty(load.Problems);
        foreach (var flow in load.Config!.Flows)
            _flows[flow.Id] = flow;
    }

    private static string FlowYaml(string id, string name, params string[] nodes) =>
        $"  {id}:\n    name: {name}\n    whenRunning: ignore\n    nodes:\n" + string.Concat(nodes.Select(n => $"      - {n}\n"));

    private static string Menu(string id, string items, string extra = "", string with = "") =>
        $"{{ id: {id}, step: menu.show, with: {{ items: [{items}]{with} }}{(extra.Length > 0 ? ", " + extra : "")} }}";

    private static string Notify(string id, string extra = "") =>
        $"{{ id: {id}, step: notify, with: {{ text: x }}{(extra.Length > 0 ? ", " + extra : "")} }}";

    private void DefineCouch(string with = "")
    {
        Define(FlowYaml("couch", "Couch", Menu("pick", "tv, desk", with: with))
            + FlowYaml("tv", "TV", Notify("a")) + FlowYaml("desk", "Desk", Notify("b")));
    }

    private Task<RunResult> Run(string flowId, CancellationToken? token = null) =>
        Bound(_runner.RunAsync(_flows[flowId], Library.Empty, token ?? TestContext.Current.CancellationToken));

    private static NodeResult NodeOf(RunResult result, string id) => result.Nodes.Single(n => n.NodeId == id);

    private static MenuOutcome Pick(string flowId) => new(flowId, MenuCloseReason.Picked);

    private static MenuOutcome Closed(MenuCloseReason reason = MenuCloseReason.Cancelled) => new(null, reason);

    [Fact]
    public async Task APickLaunchesTheFlowAsMenuAndSaysWhatWasPicked()
    {
        DefineCouch();
        _presenter.Script = _ => Pick("desk");

        var result = await Run("couch");

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.False(result.Quiet);
        Assert.Equal("Picked Desk", NodeOf(result, "pick").Summary);
        var started = Assert.Single(_starter.Started);
        Assert.Equal(("desk", "Menu · Couch", 0), (started.FlowId, started.Trigger, started.Chain));
        var request = Assert.Single(_presenter.Shown);
        Assert.Equal(("Couch", MenuLayout.List, false, TimeSpan.FromSeconds(30)), (request.Title, request.Layout, request.Large, request.CloseAfter));
        Assert.Equal([("tv", "TV", MenuItemState.Ready), ("desk", "Desk", MenuItemState.Ready)],
            request.Items.Select(i => (i.FlowId, i.Name, i.State)));
        lock (_lock)
        {
            var finished = _events.OfType<NodeFinished>().Single(f => f.Result.NodeId == "pick");
            Assert.Equal("Picked Desk", finished.Result.Summary);
            Assert.False(_events.OfType<RunFinished>().Single().Quiet);
        }
    }

    [Fact]
    public async Task TheStepsParamsReachThePresenter()
    {
        DefineCouch(", title: Evening, layout: grid, size: large, closeAfter: 2m");
        _presenter.Script = _ => Pick("tv");

        await Run("couch");

        var request = Assert.Single(_presenter.Shown);
        Assert.Equal(("Evening", MenuLayout.Grid, true, TimeSpan.FromMinutes(2)), (request.Title, request.Layout, request.Large, request.CloseAfter));
    }

    [Fact]
    public async Task ItemsShowTheirStateAndGoneOrRepeatedFlowsAreDropped()
    {
        Define(FlowYaml("couch", "Couch", Menu("pick", "tv, ghost, tv, off, desk"))
            + FlowYaml("tv", "TV", Notify("a")) + FlowYaml("desk", "Desk", Notify("b"))
            + FlowYaml("off", "Off", Notify("c")).Replace("whenRunning: ignore", "enabled: false"));
        _starter.Busy.Add("desk");
        _presenter.Script = _ => Closed();

        await Run("couch");

        Assert.Equal([("tv", MenuItemState.Ready), ("off", MenuItemState.TurnedOff), ("desk", MenuItemState.Running)],
            _presenter.Shown.Single().Items.Select(i => (i.FlowId, i.State)));
    }

    [Fact]
    public async Task APickThatWhenRunningIgnoresIsSaidSoAndStillSucceeds()
    {
        DefineCouch();
        _starter.Busy.Add("desk");
        _presenter.Script = _ => Pick("desk");

        var result = await Run("couch");

        Assert.Equal(NodeOutcome.Succeeded, NodeOf(result, "pick").Outcome);
        Assert.Equal("Desk was already running, so this pick was ignored.", NodeOf(result, "pick").Summary);
    }

    [Fact]
    public async Task ALaunchThatIsRefusedFailsTheStep()
    {
        DefineCouch();
        _starter.Refused.Add("desk");
        _presenter.Script = _ => Pick("desk");

        var result = await Run("couch");

        Assert.Equal(FlowOutcome.Failed, result.Outcome);
        Assert.Equal("Desk didn't start.", NodeOf(result, "pick").Error);
    }

    [Fact]
    public async Task TheLastPickIsFocusedNextTime()
    {
        DefineCouch();
        _presenter.Script = _ => Pick("desk");

        await Run("couch");
        await Run("couch");

        Assert.Equal([null, "desk"], _presenter.Shown.Select(r => r.LastPickedFlowId));
    }

    [Fact]
    public async Task WaitTakesTheChildsSuccess()
    {
        Define(FlowYaml("couch", "Couch", Menu("pick", "tv", with: ", wait: true"), Notify("after", "after: [pick]"))
            + FlowYaml("tv", "TV", Notify("a")));
        _presenter.Script = _ => Pick("tv");

        var result = await Run("couch");

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal("tv", NodeOf(result, "pick").Called!.FlowId);
        Assert.Equal("Picked TV", NodeOf(result, "pick").Summary);
        Assert.Empty(_starter.Started); // nested, not launched
        Assert.Contains(("after", 1), _provider.Attempts);
    }

    [Fact]
    public async Task WaitTakesAFailedChildsOutcome()
    {
        Define(FlowYaml("couch", "Couch", Menu("pick", "tv", with: ", wait: true"), Notify("after", "after: [pick]"))
            + FlowYaml("tv", "TV", Notify("a")));
        _provider.On("a", (_, _) => FakeProvider.Fail("The TV didn't answer."));
        _presenter.Script = _ => Pick("tv");

        var result = await Run("couch");

        Assert.Equal(FlowOutcome.Failed, result.Outcome);
        var pick = NodeOf(result, "pick");
        Assert.Equal((NodeOutcome.Failed, "TV didn't finish: The TV didn't answer."), (pick.Outcome, pick.Error));
        Assert.Equal(FlowOutcome.Failed, pick.Called!.Outcome);
        Assert.DoesNotContain(("after", 1), _provider.Attempts);
    }

    [Fact]
    public async Task NothingPickedEndsTheFlowQuietlyAndSkipsEvenAlwaysEdges()
    {
        Define(FlowYaml("couch", "Couch", Menu("pick", "tv, desk"), Notify("next", "after: [pick]"),
                Notify("tidy", "after: [{ id: pick, when: always }]"))
            + FlowYaml("tv", "TV", Notify("a")) + FlowYaml("desk", "Desk", Notify("b")));
        _presenter.Script = _ => Closed(MenuCloseReason.TimedOut);

        var result = await Run("couch");

        Assert.Equal(FlowOutcome.Cancelled, result.Outcome);
        Assert.True(result.Quiet);
        var pick = NodeOf(result, "pick");
        Assert.Equal((NodeOutcome.Cancelled, "Nothing picked"), (pick.Outcome, pick.Error));
        Assert.Equal(NodeOutcome.Cancelled, NodeOf(result, "next").Outcome);
        Assert.Equal(NodeOutcome.Cancelled, NodeOf(result, "tidy").Outcome);
        Assert.DoesNotContain(_provider.Attempts, a => a.Step is "next" or "tidy");
        Assert.Empty(_starter.Started);
        lock (_lock)
        {
            var finished = _events.OfType<RunFinished>().Single();
            Assert.Equal((FlowOutcome.Cancelled, true), (finished.Outcome, finished.Quiet));
        }
    }

    [Fact]
    public async Task ACancelledRunClosesTheMenuWithoutBeingQuiet()
    {
        DefineCouch();
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _presenter.Script = _ =>
        {
            cancel.Cancel();
            return null; // the window honours the token, as a presenter must
        };
        _presenter.HonourToken = true;

        var result = await Run("couch", cancel.Token);

        Assert.Equal(FlowOutcome.Cancelled, result.Outcome);
        Assert.False(result.Quiet);
        Assert.Empty(_starter.Started);
    }

    [Fact]
    public async Task TwoMenusShowOneAtATime()
    {
        Define(FlowYaml("one", "One", Menu("pick", "tv")) + FlowYaml("two", "Two", Menu("pick", "tv")) + FlowYaml("tv", "TV", Notify("a")));
        _presenter.Script = _ => Closed();
        _presenter.Delay = TimeSpan.FromMilliseconds(40);

        await Task.WhenAll(Run("one"), Run("two"));

        Assert.Equal(2, _presenter.Shown.Count);
        Assert.Equal(1, _presenter.MostAtOnce);
    }

    [Fact]
    public async Task TheGateCountsTowardTheStepsToken()
    {
        var gated = new GatedMenuPresenter(_presenter);
        var first = new TaskCompletionSource<MenuOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        _presenter.Script = _ => null;
        _presenter.Wait = first.Task;
        var request = new MenuRequest("x", [], MenuLayout.List, false, TimeSpan.FromSeconds(30), null);
        var shown = gated.ShowAsync(request, TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource();
        var waiting = gated.ShowAsync(request, cancel.Token);

        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(Hung, TestContext.Current.CancellationToken));
        first.SetResult(Closed());
        await Bound(shown);
        Assert.Single(_presenter.Shown); // the one that waited never reached the window
    }

    [Fact]
    public async Task AWaitingParentPickingASubmenuShowsItsMenuAndTakesItsOutcome()
    {
        Define(FlowYaml("parent", "Parent", Menu("pick", "child", with: ", wait: true"))
            + FlowYaml("child", "Child", Menu("pick", "leaf")) + FlowYaml("leaf", "Leaf", Notify("a")));
        _presenter.Script = request => request.Title == "Parent" ? Pick("child") : Pick("leaf");

        var result = await Run("parent");

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal(["Parent", "Child"], _presenter.Shown.Select(r => r.Title)); // the gate was free for the child's menu
        var called = NodeOf(result, "pick").Called!;
        Assert.Equal("child", called.FlowId);
        Assert.Equal("Picked Leaf", called.Nodes.Single().Summary);
        var started = Assert.Single(_starter.Started);
        Assert.Equal(("leaf", "Menu · Child"), (started.FlowId, started.Trigger));
    }

    [Fact]
    public async Task AChildMenuClosedWithNothingPickedEndsTheParentQuietly()
    {
        Define(FlowYaml("parent", "Parent", Menu("pick", "child", with: ", wait: true"), Notify("after", "after: [pick]"))
            + FlowYaml("child", "Child", Menu("pick", "leaf")) + FlowYaml("leaf", "Leaf", Notify("a")));
        _presenter.Script = request => request.Title == "Parent" ? Pick("child") : Closed();

        var result = await Run("parent");

        Assert.Equal(FlowOutcome.Cancelled, result.Outcome);
        Assert.True(result.Quiet);
        Assert.Equal(NodeOutcome.Cancelled, NodeOf(result, "pick").Outcome);
    }

    [Fact]
    public async Task BackToAParentThatIsStillRunningFollowsItsWhenRunning()
    {
        Define(FlowYaml("parent", "Parent", Menu("pick", "child", with: ", wait: true"))
            + FlowYaml("child", "Child", Menu("pick", "parent")));
        var calls = new FlowCalls(() => ConfigSnapshot.Empty with
        {
            Flows = [.. _flows.Values.Select(f => new FlowState(f.Id, f, []) { Written = f })],
        });
        var runner = new FlowRunner([new MenuProvider(new GatedMenuPresenter(_presenter), calls)], _time) { Flows = calls };
        var launcher = new FlowLauncher(runner);
        calls.Launcher = launcher;
        _presenter.Script = request => request.Title == "Parent" ? Pick("child") : Pick("parent");

        var result = await Bound(launcher.TriggerAsync(_flows["parent"], Library.Empty, "Tray")) ?? throw new InvalidOperationException();

        var back = NodeOf(result, "pick").Called!.Nodes.Single();
        Assert.Equal("Parent was already running, so this pick was ignored.", back.Summary);
        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, _presenter.Shown.Count); // the parent's menu did not open a second time
    }

    [Fact]
    public async Task ADryRunDoesNotShowAndSaysHowManyChoices()
    {
        DefineCouch();

        var result = await Bound(EngineController.CreateDryRunner(_time).RunAsync(_flows["couch"], Library.Empty, TestContext.Current.CancellationToken));

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal("Would show 2 choices", NodeOf(result, "pick").Summary);
        Assert.Empty(_presenter.Shown);
        Assert.Empty(_starter.Started);
    }

    [Fact]
    public async Task ATestStepShowsTheRealMenuAndReturnsThePickWithoutRunningIt()
    {
        DefineCouch();
        _presenter.Script = _ => Pick("desk");
        const string yaml = """
            flows:
              couch:
                name: Couch
                nodes:
                  - { id: pick, step: menu.show, with: { items: [tv, desk] } }
              tv:
                nodes: [{ id: a, step: notify, with: { text: x } }]
              desk:
                nodes: [{ id: b, step: notify, with: { text: x } }]
            """;
        var root = @"Software\DazeControl.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            var controller = new EngineController(() => ConfigSnapshot.Empty, new FlowLauncher(_runner), new RunHistory(null),
                EngineController.CreateDryRunner(_time), new Autostart(root + @"\Run", root + @"\Approved"),
                new EngineInfo("1.2.3", 42, @"C:\x\DazeControl.exe", "c.yaml"), _ => { }, stepRunner: _runner);

            var answer = await Bound(controller.HandleAsync(
                new PipeRequest { Id = 5, Op = Ops.TestStep, Flow = "couch", Node = "pick", Config = yaml }, CancellationToken.None));

            Assert.True(answer.Ok, answer.Error);
            Assert.Equal("You picked Desk. Testing doesn't run it.", Assert.Single(answer.Run!.Nodes).Summary);
            Assert.Single(_presenter.Shown);
            Assert.Empty(_starter.Started);
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(root, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public void TheRunPopupSaysNothingForAQuietEndOrAMenuOnlyFlow()
    {
        var runId = Guid.NewGuid();
        var t0 = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        DefineCouch();
        var menuOnly = _flows["couch"];
        var mixed = menuOnly with { Nodes = [.. menuOnly.Nodes, new StepNode("after", "notify") { After = [new("pick")] }] };

        var model = new RunPopupModel();
        Assert.False(model.Apply(new RunStarted(runId, "couch", t0, 1) { Flow = menuOnly }, null, Settings));
        Assert.False(model.Apply(new NodeStarted(runId, "couch", t0, "pick", 1), null, Settings));
        Assert.False(model.Apply(new RunFinished(runId, "couch", t0, FlowOutcome.Succeeded), null, Settings));
        Assert.Null(model.Current);

        var other = Guid.NewGuid();
        Assert.False(model.Apply(new RunStarted(other, "couch", t0, 2) { Flow = mixed }, null, Settings));
        Assert.False(model.Apply(new NodeStarted(other, "couch", t0, "pick", 1), null, Settings));
        Assert.False(model.Apply(new NodeFinished(other, "couch", t0, new NodeResult("pick", NodeOutcome.Cancelled)), null, Settings));
        Assert.False(model.Apply(new RunFinished(other, "couch", t0, FlowOutcome.Cancelled) { Quiet = true }, null, Settings));
        Assert.Null(model.Current);

        var shown = Guid.NewGuid();
        Assert.False(model.Apply(new RunStarted(shown, "couch", t0, 2) { Flow = mixed }, null, Settings));
        Assert.True(model.Apply(new NodeStarted(shown, "couch", t0, "after", 1), null, Settings)); // a real step: the pop-up comes
        Assert.Equal("Couch", model.Current!.Title);
    }

    private sealed class FakePresenter : IMenuPresenter
    {
        private int _now;

        public Func<MenuRequest, MenuOutcome?>? Script { get; set; }

        public TimeSpan Delay { get; set; }

        public Task<MenuOutcome>? Wait { get; set; }

        public bool HonourToken { get; set; }

        public int MostAtOnce { get; private set; }

        public List<MenuRequest> Shown { get; } = [];

        public async Task<MenuOutcome> ShowAsync(MenuRequest request, CancellationToken cancellationToken)
        {
            lock (Shown)
            {
                Shown.Add(request);
                MostAtOnce = Math.Max(MostAtOnce, ++_now);
            }
            try
            {
                if (Wait is { } wait)
                    return await wait.ConfigureAwait(false);
                var outcome = Script!(request);
                if (Delay > TimeSpan.Zero)
                    await Task.Delay(Delay, CancellationToken.None).ConfigureAwait(false);
                if (HonourToken)
                    cancellationToken.ThrowIfCancellationRequested();
                return outcome ?? Closed();
            }
            finally
            {
                lock (Shown)
                    _now--;
            }
        }
    }

    private sealed class Starter(MenuProviderTests test) : IFlowStarter
    {
        public HashSet<string> Busy { get; } = [];

        public HashSet<string> NeverValid { get; } = [];

        public HashSet<string> Refused { get; } = [];

        public List<(string FlowId, string Trigger, int Chain)> Started { get; } = [];

        public CalledFlow Resolve(string flowId)
        {
            if (NeverValid.Contains(flowId))
                return new(null, Library.Empty, flowId, $"{flowId} didn't start. It needs fixing.", CalledWhy.NeedsFixing);
            if (!test._flows.TryGetValue(flowId, out var flow))
                return new(null, Library.Empty, flowId, $"{flowId} isn't there anymore. Pick another flow.", CalledWhy.Missing);
            return flow.Enabled ? new(flow, Library.Empty, flow.Name, null)
                : new(null, Library.Empty, flow.Name, $"{flow.Name} is turned off, so it didn't run. Turn it on in Flows.", CalledWhy.TurnedOff);
        }

        public bool IsRunning(string flowId) => Busy.Contains(flowId);

        public StartAnswer Start(string flowId, string trigger, IReadOnlyList<string> chain, IReadOnlyDictionary<string, string> inputs, string callerName)
        {
            var called = Resolve(flowId);
            if (called.Flow is null || Refused.Contains(flowId))
                return new(false, called.Refusal ?? $"{called.Name} didn't start.");
            lock (Started)
                Started.Add((flowId, trigger, chain.Count));
            return Busy.Contains(flowId) ? new(true, $"{called.Name} was already running, so it wasn't started again.") : new(true);
        }
    }
}
