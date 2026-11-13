using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Yaml;
using DazeControl.Runtime.Control;
using DazeControl.Runtime.Providers;
using static DazeControl.Runtime.Tests.FlowRunnerTests;

namespace DazeControl.Runtime.Tests;

/// <summary>T24.1, D-133: <c>flow.stop</c> at run time, top level and called. Fake providers only.</summary>
public sealed class StopFlowTests : IDisposable
{
    private readonly ManualTime _time = new();
    private readonly FakeProvider _provider = new();
    private readonly Dictionary<string, Flow> _flows = [];
    private readonly List<RunEvent> _events = [];
    private readonly Lock _lock = new();
    private readonly FlowRunner _runner;

    public StopFlowTests()
    {
        var starter = new Starter(_flows);
        _runner = new FlowRunner([_provider, new StopProvider(), new FlowProvider(starter)], _time, events: e =>
        {
            lock (_lock)
                _events.Add(e);
        })
        { Flows = starter };
    }

    public void Dispose() => _time.Dispose();

    private void Define(string id, string name, params string[] nodes)
    {
        var yaml = $"flows:\n  {id}:\n    name: {name}\n    nodes:\n" + string.Concat(nodes.Select(n => $"      - {n}\n"));
        var load = ConfigYaml.Load(yaml);
        Assert.Empty(load.Problems);
        _flows[id] = load.Config!.Flows.Single();
    }

    private static string Step(string id, string extra = "") => $"{{ id: {id}, step: notify, with: {{ text: x }}{(extra.Length > 0 ? ", " + extra : "")} }}";

    private static string Stop(string id, string mode, string message, string extra) =>
        $"{{ id: {id}, step: flow.stop, with: {{ mode: {mode}, message: \"{message}\" }}, {extra} }}";

    private static string Call(string id, string flow) => $"{{ id: {id}, step: flow.run, with: {{ flow: {flow} }} }}";

    private Task<RunResult> Run(string id) => Bound(_runner.RunAsync(_flows[id], Library.Empty, TestContext.Current.CancellationToken));

    private static NodeResult NodeOf(RunResult result, string id) => result.Nodes.Single(n => n.NodeId == id);

    [Fact]
    public async Task AQuietStopAfterACheckFailureEndsTheFlowWithNothingToShow()
    {
        _provider.On("chk", (_, _) => FakeProvider.Fail("Not at home"));
        Define("f", "Guard", Step("chk", "onFailure: skip"),
            Stop("stop", "quiet", "", "after: [{ id: chk, when: failure }]"), Step("work", "after: [chk]"));

        var result = await Run("f");

        Assert.True(result.Quiet); // the pop-up shows nothing
        Assert.Equal(FlowOutcome.Cancelled, result.Outcome); // not Failed: no tray problem glyph (EngineHost skips Cancelled)
        var stop = NodeOf(result, "stop");
        Assert.Equal((NodeOutcome.Cancelled, "Stopped here"), (stop.Outcome, stop.Error));
        Assert.Equal("Stopped here", stop.Summary);
        Assert.DoesNotContain("work", _provider.Attempts.Select(a => a.Step));
        lock (_lock)
            Assert.True(_events.OfType<RunFinished>().Single().Quiet);
    }

    [Fact]
    public async Task AQuietStopKeepsItsMessage()
    {
        Define("f", "Guard", Stop("stop", "quiet", "Not at home", "after: []"));

        var result = await Run("f");

        Assert.Equal("Stopped here: Not at home", NodeOf(result, "stop").Error);
    }

    [Fact]
    public async Task AQuietStopCancelsStepsStillRunningInOtherBranches()
    {
        _provider.On("slow", (_, token) => FakeProvider.Hang(token));
        _provider.On("gate", async (_, _) => await _provider.Started("slow"));
        Define("f", "Branches", Step("slow"), Step("gate"), Stop("stop", "quiet", "", "after: [gate]"));

        var result = await Run("f");

        Assert.Equal(NodeOutcome.Cancelled, NodeOf(result, "slow").Outcome);
        Assert.True(result.Quiet);
        Assert.Equal(FlowOutcome.Cancelled, result.Outcome);
    }

    [Fact]
    public async Task AStopThatFinishesAParallelCardLastStartsNothingAfterTheCard()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new FlowRunner([_provider, new GatedStop(release.Task)], _time);
        Define("f", "Card", Step("a"), "{ id: s, step: flow.stop }", "{ id: card, group: [a, s] }", Step("after", "after: [card]"));
        // The other member finishes first; the stop is the last one to finish and completes the card.
        var run = Bound(runner.RunAsync(_flows["f"], Library.Empty, TestContext.Current.CancellationToken));
        await _provider.Started("a");
        release.SetResult();

        var result = await run;

        Assert.DoesNotContain("after", _provider.Attempts.Select(a => a.Step));
        Assert.True(result.Quiet);
    }

    [Fact]
    public async Task AQuietStopInACalledFlowEndsOnlyThatFlowAndTheCallersStepIsDone()
    {
        Define("mk", "Make it the default", Stop("stop", "quiet", "", "after: []"), Step("never", "after: [stop]"));
        Define("top", "Headphones", Call("call", "mk"), Step("after", "after: [call]"));

        var result = await Run("top");

        var call = NodeOf(result, "call");
        Assert.Equal(NodeOutcome.Succeeded, call.Outcome);
        Assert.Equal(NodeOutcome.Cancelled, call.Called!.Nodes.Single(n => n.NodeId == "stop").Outcome);
        Assert.Equal(NodeOutcome.Succeeded, NodeOf(result, "after").Outcome); // the caller carries on
        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.False(result.Quiet);
        Assert.DoesNotContain("never", _provider.Attempts.Select(a => a.Step));
    }

    [Fact]
    public async Task AStopAsFailedInACalledFlowFailsTheCallersStepWithTheMessage()
    {
        Define("mk", "Make it the default", Stop("stop", "failed", "Not at home", "after: []"));
        Define("top", "Headphones", Call("call", "mk"), Step("after", "after: [call]"));

        var result = await Run("top");

        var call = NodeOf(result, "call");
        Assert.Equal(NodeOutcome.Failed, call.Outcome);
        Assert.Equal("Make it the default didn't finish: Not at home", call.Error);
        Assert.Equal(NodeOutcome.Cancelled, NodeOf(result, "after").Outcome);
        Assert.Equal(FlowOutcome.Failed, result.Outcome);
    }

    [Fact]
    public async Task AStopAsFailedFailsTheFlowAndShowsItsMessage()
    {
        Define("f", "Guard", Stop("stop", "failed", "Not at home", "after: []"), Step("later", "after: [stop]"));

        var result = await Run("f");

        Assert.False(result.Quiet);
        Assert.Equal(FlowOutcome.Failed, result.Outcome);
        Assert.Equal("Not at home", NodeOf(result, "stop").Error);
    }

    [Fact]
    public async Task AStopAsFailedWithoutAMessageSaysSomething()
    {
        Define("f", "Guard", Stop("stop", "failed", "", "after: []"));

        var result = await Run("f");

        Assert.Equal("The flow was stopped here.", NodeOf(result, "stop").Error);
    }

    [Fact]
    public async Task AMenuClosedWithNothingPickedStillEndsTheCallerToo()
    {
        // D-054 unchanged: only a Stop flow step is "done" for the caller.
        var runner = new FlowRunner([_provider, new EndsFlow(), new FlowProvider(new Starter(_flows))], _time) { Flows = new Starter(_flows) };
        Define("mk", "Menu", "{ id: m, step: menu.show }");
        Define("top", "Top", Call("call", "mk"), Step("after", "after: [call]"));

        var result = await Bound(runner.RunAsync(_flows["top"], Library.Empty, TestContext.Current.CancellationToken));

        Assert.True(result.Quiet);
        Assert.False(result.Stopped);
        Assert.DoesNotContain("after", _provider.Attempts.Select(a => a.Step));
    }

    [Fact]
    public async Task ADryRunAndAStepTestSayWhatWouldHappenAndStopNothing()
    {
        Define("f", "Guard", Stop("stop", "quiet", "", "after: []"), Stop("fail", "failed", "Not at home", "after: []"));
        var quiet = (StepNode)_flows["f"].FindNode("stop")!;
        var failed = (StepNode)_flows["f"].FindNode("fail")!;
        Assert.Equal("Would stop here", StopProvider.DrySummary(quiet));
        Assert.Equal("Would stop here as failed: Not at home", StopProvider.DrySummary(failed));

        var testFlow = _flows["f"] with { Id = EngineController.TestPrefix + "x" };
        var context = new StepContext(testFlow, quiet, ProviderCatalog.Find(ProviderCatalog.StopFlowId)!, Library.Empty, 1);
        await new StopProvider().ExecuteAsync(context, CancellationToken.None);
        Assert.Equal("Testing doesn't stop anything. Would stop here", context.Summary);
    }

    /// <summary>The real stop, held back until released, so it can be the last member of a card to finish.</summary>
    private sealed class GatedStop(Task gate) : IStepProvider
    {
        public string Id => ProviderCatalog.StopFlowId;

        public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            await new StopProvider().ExecuteAsync(context, cancellationToken);
        }
    }

    private sealed class EndsFlow : IStepProvider
    {
        public string Id => ProviderCatalog.MenuShowId;

        public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken) => throw new StepEndsFlowException("Nothing picked");
    }

    private sealed class Starter(Dictionary<string, Flow> flows) : IFlowStarter
    {
        public CalledFlow Resolve(string flowId) =>
            flows.TryGetValue(flowId, out var flow) ? new(flow, Library.Empty, flow.Name, null) : new(null, Library.Empty, flowId, $"{flowId} isn't there anymore.");

        public bool IsRunning(string flowId) => false;

        public StartAnswer Start(string flowId, string trigger, IReadOnlyList<string> chain, IReadOnlyDictionary<string, string> inputs, string callerName) =>
            new(false, "not used");
    }
}
