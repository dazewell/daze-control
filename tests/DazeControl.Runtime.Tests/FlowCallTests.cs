using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Yaml;
using DazeControl.Runtime.Control;
using DazeControl.Runtime.Popup;
using DazeControl.Runtime.Providers;
using static DazeControl.Runtime.Tests.FlowRunnerTests;

namespace DazeControl.Runtime.Tests;

/// <summary><c>flow.run</c>: nested runs (DESIGN §2.2, §3), start mode and the loop guards (§4).</summary>
public sealed class FlowCallTests : IDisposable
{
    private readonly ManualTime _time = new();
    private readonly FakeProvider _provider = new();
    private readonly Dictionary<string, Flow> _flows = [];
    private readonly List<RunEvent> _events = [];
    private readonly Lock _lock = new();
    private readonly FakeStarter _starter;
    private readonly FlowRunner _runner;

    public FlowCallTests()
    {
        _starter = new FakeStarter(this);
        // "notify" holds a lock here, so a nested step and a top-level one can be seen sharing it.
        var catalog = ProviderCatalog.All.Select(d => d.Id == "notify" ? d with { Resources = ["message"] } : d).ToList();
        _runner = new FlowRunner([_provider, new FlowProvider(_starter)], _time, catalog: catalog, events: e =>
        {
            lock (_lock)
                _events.Add(e);
        })
        { Flows = _starter };
    }

    public void Dispose() => _time.Dispose();

    /// <summary>Flows from YAML (not validated: the guards must hold for shapes validation would refuse, D-021).</summary>
    private void Define(string yaml)
    {
        var load = ConfigYaml.Load("flows:\n" + yaml);
        Assert.Empty(load.Problems);
        foreach (var flow in load.Config!.Flows)
            _flows[flow.Id] = flow;
    }

    private static string Notify(string id, string extra = "") =>
        $"{{ id: {id}, step: notify, with: {{ text: x }}{(extra.Length > 0 ? ", " + extra : "")} }}";

    private static string Call(string id, string flow, string extra = "", string with = "") =>
        $"{{ id: {id}, step: flow.run, with: {{ flow: {flow}{with} }}{(extra.Length > 0 ? ", " + extra : "")} }}";

    private static string FlowYaml(string id, string name, params string[] nodes) =>
        $"  {id}:\n    name: {name}\n    nodes:\n" + string.Concat(nodes.Select(n => $"      - {n}\n"));

    private Task<RunResult> Run(string flowId, CancellationToken? token = null) =>
        Bound(_runner.RunAsync(_flows[flowId], Library.Empty, token ?? TestContext.Current.CancellationToken));

    private static NodeResult NodeOf(RunResult result, string id) => result.Nodes.Single(n => n.NodeId == id);

    /// <summary>Caller "top": call → after (success) and fix (failure).</summary>
    private void DefineCaller(params string[] called)
    {
        Define(FlowYaml("top", "Headphones",
            Call("call", "mk", "onFailure: skip"),
            Notify("after", "after: [call]"),
            Notify("fix", "after: [{ id: call, when: failure }]"))
            + FlowYaml("mk", "Make it the default", called));
    }

    [Fact]
    public async Task SucceededCalledFlowSucceedsTheStepAndKeepsItsResults()
    {
        DefineCaller(Notify("set"));

        var result = await Run("top");

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        var call = NodeOf(result, "call");
        Assert.Equal(NodeOutcome.Succeeded, call.Outcome);
        Assert.Equal(("mk", "Make it the default", FlowOutcome.Succeeded), (call.Called!.FlowId, call.Called.FlowName, call.Called.Outcome));
        Assert.Equal(NodeOutcome.Succeeded, Assert.Single(call.Called.Nodes).Outcome);
        Assert.Equal(NodeOutcome.Succeeded, NodeOf(result, "after").Outcome);
        Assert.Equal(NodeOutcome.NotNeeded, NodeOf(result, "fix").Outcome);
    }

    [Fact]
    public async Task AFailureTheCalledFlowsFallbackFixedIsASuccess()
    {
        _provider.On("set", (_, _) => FakeProvider.Fail("no"));
        DefineCaller(Notify("set", "onFailure: skip"), Notify("connect", "after: [{ id: set, when: failure }]"));

        var result = await Run("top");

        Assert.Equal(FlowOutcome.Succeeded, NodeOf(result, "call").Called!.Outcome);
        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal(NodeOutcome.Succeeded, NodeOf(result, "after").Outcome);
    }

    [Fact]
    public async Task APartialCalledFlowSucceedsTheStepButMakesTheCallerPartial()
    {
        _provider.On("set", (_, _) => FakeProvider.Fail("no"));
        DefineCaller(Notify("set", "onFailure: ignore"), Notify("more"));

        var result = await Run("top");

        var call = NodeOf(result, "call");
        Assert.Equal(NodeOutcome.Succeeded, call.Outcome);
        Assert.True(call.IsPartialCall);
        Assert.Equal(NodeOutcome.Succeeded, NodeOf(result, "after").Outcome); // success edges fire
        Assert.Equal(NodeOutcome.NotNeeded, NodeOf(result, "fix").Outcome); // no failure edge
        Assert.Equal(FlowOutcome.PartiallySucceeded, result.Outcome);
    }

    [Fact]
    public async Task AFailedCalledFlowFailsTheStepInItsOwnWords()
    {
        _provider.On("connect", (_, _) => FakeProvider.Fail("Bluetooth didn't connect in 20 s."));
        DefineCaller(Notify("connect"), Notify("again", "after: [connect]"));

        var result = await Run("top");

        var call = NodeOf(result, "call");
        Assert.Equal(NodeOutcome.Failed, call.Outcome);
        Assert.Equal("Make it the default didn't finish: Bluetooth didn't connect in 20 s.", call.Error);
        Assert.Equal(FlowOutcome.Failed, call.Called!.Outcome);
        Assert.Equal(NodeOutcome.Succeeded, NodeOf(result, "fix").Outcome); // failure edge
        Assert.Equal(NodeOutcome.Skipped, NodeOf(result, "after").Outcome);
        Assert.Equal(FlowOutcome.Succeeded, result.Outcome); // its fallback fixed it (D-065)
    }

    [Fact]
    public async Task AFailedCallWithoutAFallbackStopsTheCaller()
    {
        _provider.On("set", (_, _) => FakeProvider.Fail("no"));
        Define(FlowYaml("top", "Top", Call("call", "mk"), Notify("after", "after: [call]"))
            + FlowYaml("mk", "Mk", Notify("set")));

        var result = await Run("top");

        Assert.Equal(FlowOutcome.Failed, result.Outcome);
        Assert.Equal(NodeOutcome.Cancelled, NodeOf(result, "after").Outcome);
    }

    [Fact]
    public async Task TimeoutFailsTheStepAndCancelsTheNestedRun()
    {
        _provider.On("hang", (_, token) => FakeProvider.Hang(token));
        Define(FlowYaml("top", "Top", Call("call", "mk", "timeout: 1m")) + FlowYaml("mk", "Make it the default", Notify("hang", "timeout: 1h")));

        var run = Run("top");
        await _provider.Started("hang");
        _time.Advance(TimeSpan.FromMinutes(1));
        var result = await run;

        var call = NodeOf(result, "call");
        Assert.Equal(NodeOutcome.Failed, call.Outcome);
        Assert.Equal("Make it the default didn't finish in 1m.", call.Error);
        Assert.Equal(FlowOutcome.Cancelled, call.Called!.Outcome);
        Assert.Equal(NodeOutcome.Cancelled, Assert.Single(call.Called.Nodes).Outcome);
    }

    [Fact]
    public async Task CancellingTheCallerCancelsTheNestedRun()
    {
        _provider.On("hang", (_, token) => FakeProvider.Hang(token));
        DefineCaller(Notify("hang"));
        using var cancel = new CancellationTokenSource();

        var run = Run("top", cancel.Token);
        await _provider.Started("hang");
        await cancel.CancelAsync();
        var result = await run;

        Assert.Equal(FlowOutcome.Cancelled, result.Outcome);
        Assert.Equal(NodeOutcome.Cancelled, NodeOf(result, "call").Outcome);
        Assert.Equal(FlowOutcome.Cancelled, NodeOf(result, "call").Called!.Outcome);
    }

    [Theory]
    [InlineData("gone", "gone isn't there anymore. Pick another flow.")]
    [InlineData("off", "Off is turned off, so it didn't run. Turn it on in Flows.")]
    [InlineData("broken", "broken didn't start. It needs fixing.")]
    public async Task ANotRunnableFlowFailsTheStep(string flowId, string error)
    {
        Define(FlowYaml("top", "Top", Call("call", flowId, "onFailure: skip"), Notify("fix", "after: [{ id: call, when: failure }]"))
            + FlowYaml("off", "Off", Notify("s")) + "    enabled: false\n");
        _starter.NeverValid.Add("broken");

        var result = await Run("top");

        Assert.Equal((NodeOutcome.Failed, error), (NodeOf(result, "call").Outcome, NodeOf(result, "call").Error));
        Assert.Equal(NodeOutcome.Succeeded, NodeOf(result, "fix").Outcome);
    }

    [Fact]
    public async Task RetriesRunTheWholeFlowAgain()
    {
        var tries = 0;
        _provider.On("set", (_, _) => Interlocked.Increment(ref tries) == 1 ? FakeProvider.Fail("no") : Task.CompletedTask);
        Define(FlowYaml("top", "Top", Call("call", "mk", "retry: { count: 1, delay: 0s }")) + FlowYaml("mk", "Mk", Notify("first"), Notify("set", "after: [first]")));

        var result = await Run("top");

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, NodeOf(result, "call").Attempts);
        Assert.Equal(2, _provider.Attempts.Count(a => a.Step == "first"));
    }

    [Fact]
    public async Task NestedEventsCarryTheParentRun()
    {
        DefineCaller(Notify("set"));

        var result = await Run("top");

        List<RunEvent> events;
        lock (_lock)
            events = [.. _events];
        var nested = events.Where(e => e.FlowId == "mk").ToList();
        Assert.Equal(4, nested.Count); // started, node started, node finished, finished
        Assert.All(nested, e => Assert.Equal((result.RunId, "call"), (e.ParentRunId!.Value, e.ParentNodeId!)));
        Assert.All(events.Where(e => e.FlowId == "top"), e => Assert.Null(e.ParentRunId));
        Assert.Equal(NodeOf(result, "call").Called!.RunId, nested[0].RunId);
    }

    [Fact]
    public async Task ACallOfAFlowAlreadyOnTheChainFailsAtOnce()
    {
        // Validation refuses this, but an old valid version beside a new one can make it (D-021).
        Define(FlowYaml("a", "A", Call("call", "b")) + FlowYaml("b", "B", Call("back", "a")));

        var result = await Run("a");

        var inner = NodeOf(result, "call").Called!.Nodes.Single();
        Assert.Equal("Didn't run A: it's already running further up, so that would loop.", inner.Error);
        Assert.Equal(FlowOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task ChainsDeeperThanEightStop()
    {
        var yaml = string.Concat(Enumerable.Range(1, 9).Select(i => FlowYaml($"f{i}", $"F{i}", i < 9 ? Call("call", $"f{i + 1}") : Notify("s"))));
        Define(yaml);

        var result = await Run("f1");

        var node = NodeOf(result, "call");
        for (var depth = 2; depth <= 8; depth++)
            node = node.Called!.Nodes.Single();
        Assert.Equal("Flows ran inside each other more than 8 deep, so this one stopped.", node.Error);
        Assert.Null(node.Called);
        Assert.DoesNotContain(_provider.Attempts, a => a.Step == "s");
    }

    [Fact]
    public async Task TwoCallsOfOneFlowRunAtOnce()
    {
        var both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;
        _provider.On("w", async (_, token) =>
        {
            if (Interlocked.Increment(ref started) == 2)
                both.TrySetResult();
            await both.Task.WaitAsync(token);
        });
        // "w" is a wait-free step type without a lock, so the two nested runs don't queue on "message".
        Define(FlowYaml("top", "Top", Call("one", "mk"), Call("two", "mk")) + FlowYaml("mk", "Mk", "{ id: w, step: flag.set, with: { name: x } }"));
        var flagProvider = new RenamedProvider(_provider, "flag.set");
        var runner = new FlowRunner([_provider, flagProvider], _time) { Flows = _starter };

        var result = await Bound(runner.RunAsync(_flows["top"], Library.Empty, TestContext.Current.CancellationToken));

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal(2, started);
    }

    [Fact]
    public async Task ANestedStepsLockBlocksATopLevelStep()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var innerRunning = 0;
        var overlapped = false;
        _provider.On("inner", async (_, token) =>
        {
            Volatile.Write(ref innerRunning, 1);
            await release.Task.WaitAsync(token);
            Volatile.Write(ref innerRunning, 0);
        });
        _provider.On("outer", (_, _) =>
        {
            overlapped |= Volatile.Read(ref innerRunning) == 1;
            return Task.CompletedTask;
        });
        // Both start together; whichever takes "message" first, the other waits for it.
        Define(FlowYaml("top", "Top", Call("call", "mk"), Notify("outer")) + FlowYaml("mk", "Mk", Notify("inner")));

        var run = Run("top");
        await _provider.Started("inner");
        await Task.Delay(100, TestContext.Current.CancellationToken);
        release.SetResult();
        var result = await run;

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Contains(_provider.Attempts, a => a.Step == "outer");
        Assert.False(overlapped);
    }

    [Fact]
    public async Task StartModeStartsTheFlowAndCarriesOn()
    {
        Define(FlowYaml("top", "Headphones", Call("call", "mk", with: ", wait: false"), Notify("after", "after: [call]")) + FlowYaml("mk", "Mk", Notify("s")));

        var result = await Run("top");

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Null(NodeOf(result, "call").Called);
        Assert.Equal([("mk", "Flow · Headphones", "top")], _starter.Started.Select(s => (s.FlowId, s.Trigger, string.Join(">", s.Chain))));
        Assert.DoesNotContain(_provider.Attempts, a => a.Step == "s"); // not nested: the launcher runs it
    }

    [Fact]
    public async Task StartModeIgnoredIsASuccessAndRefusedIsAFailure()
    {
        Define(FlowYaml("top", "Top", Call("ok", "busy", with: ", wait: false"), Call("no", "gone", "onFailure: ignore", ", wait: false"))
            + FlowYaml("busy", "Busy", Notify("s")));
        _starter.Busy.Add("busy");

        var result = await Run("top");

        Assert.Equal(NodeOutcome.Succeeded, NodeOf(result, "ok").Outcome);
        Assert.Equal((NodeOutcome.FailedIgnored, "gone isn't there anymore. Pick another flow."), (NodeOf(result, "no").Outcome, NodeOf(result, "no").Error));
    }

    [Fact]
    public void StartModePassesTheChainOnSoAStartLoopIsCaught()
    {
        var calls = new FlowCalls(() => ConfigSnapshot.Empty);
        Assert.Equal("Didn't run A: it's already running further up, so that would loop.", FlowCalls.Guard(["a", "b"], "a", "A"));
        Assert.Null(FlowCalls.Guard(["a", "b"], "c", "C"));
        Assert.False(calls.Start("a", "x", ["b"], new Dictionary<string, string>()).Started); // not in the (empty) config
    }

    [Fact]
    public void AFlowTurnedOffInTheFileNeverRunsItsOlderVersion()
    {
        var old = new Flow("mk", "Mk", []);
        var state = new FlowState("mk", old, [new(Core.Validation.Severity.Error, "flows.mk.nodes", "bad", "mk")])
        {
            Written = old with { Enabled = false },
        };
        var calls = new FlowCalls(() => ConfigSnapshot.Empty with { Flows = [state] });

        var called = calls.Resolve("mk");

        Assert.Null(called.Flow);
        Assert.Equal("Mk is turned off, so it didn't run. Turn it on in Flows.", called.Refusal);
    }

    [Fact]
    public async Task DryRunDoesNotDescend()
    {
        Define(FlowYaml("top", "Top", Call("call", "mk")) + FlowYaml("mk", "Mk", Notify("s")));

        var result = await Bound(EngineController.CreateDryRunner(_time).RunAsync(_flows["top"], Library.Empty, TestContext.Current.CancellationToken));

        Assert.Equal(NodeOutcome.Succeeded, NodeOf(result, "call").Outcome);
        Assert.Null(NodeOf(result, "call").Called);
    }

    private PopupView? Popup()
    {
        var model = new RunPopupModel();
        List<RunEvent> events;
        lock (_lock)
            events = [.. _events];
        foreach (var e in events)
            model.Apply(e, null, new PopupSettings());
        return model.Current;
    }

    [Fact]
    public async Task ThePopUpShowsTheNestedCurrentStepAndOnlyTheTopRun()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _provider.On("connect", (_, token) => release.Task.WaitAsync(token));
        DefineCaller(Notify("set"), "{ id: connect, name: Connecting Sony, step: notify, with: { text: x }, after: [set] }");

        var run = Run("top");
        await _provider.Started("connect");
        var view = Popup()!;
        release.SetResult();
        await run;

        Assert.Equal(("Headphones", "Make it the default · Connecting Sony…", "0 of 2"), (view.Title, view.Detail, view.Status));
        Assert.InRange(view.Progress, 0.2, 0.3); // half of the call step's share
        Assert.Equal(("Headphones", "Done"), (Popup()!.Title, Popup()!.Status));
    }

    [Fact]
    public async Task APartialCallSaysTheCalledFlowFirst()
    {
        _provider.On("connect", (_, _) => FakeProvider.Fail("no"));
        DefineCaller("{ id: connect, name: Connect, step: notify, with: { text: x }, onFailure: ignore }", Notify("more"));

        await Run("top");

        var view = Popup()!;
        Assert.Equal(("Done, with a problem", "Make it the default: Connect didn't work, so it went on without it."), (view.Headline, view.Detail));
    }

    /// <summary>Flows from the test's YAML; "Busy" flows ignore a start; "NeverValid" ones have no runnable version.</summary>
    private sealed class FakeStarter(FlowCallTests test) : IFlowStarter
    {
        public HashSet<string> Busy { get; } = [];

        public HashSet<string> NeverValid { get; } = [];

        public List<(string FlowId, string Trigger, IReadOnlyList<string> Chain)> Started { get; } = [];

        public CalledFlow Resolve(string flowId)
        {
            if (NeverValid.Contains(flowId))
                return new(null, Library.Empty, flowId, $"{flowId} didn't start. It needs fixing.");
            if (!test._flows.TryGetValue(flowId, out var flow))
                return new(null, Library.Empty, flowId, $"{flowId} isn't there anymore. Pick another flow.");
            return flow.Enabled ? new(flow, Library.Empty, flow.Name, null)
                : new(null, Library.Empty, flow.Name, $"{flow.Name} is turned off, so it didn't run. Turn it on in Flows.");
        }

        public StartAnswer Start(string flowId, string trigger, IReadOnlyList<string> chain, IReadOnlyDictionary<string, string> inputs)
        {
            var called = Resolve(flowId);
            if (called.Flow is null)
                return new(false, called.Refusal);
            lock (Started)
                Started.Add((flowId, trigger, chain));
            return Busy.Contains(flowId) ? new(true, $"{called.Name} was already running, so it wasn't started again.") : new(true);
        }
    }

    /// <summary>The fake's scripts under another step type (one without a lock).</summary>
    private sealed class RenamedProvider(FakeProvider inner, string id) : IStepProvider
    {
        public string Id => id;

        public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken) => inner.ExecuteAsync(context, cancellationToken);
    }
}
