using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Core.Runs;
using CtrlAltDaze.Runtime.Control;
using CtrlAltDaze.Runtime.Triggers;
using static CtrlAltDaze.Runtime.Tests.SchedulerTests;

namespace CtrlAltDaze.Runtime.Tests;

/// <summary>T26.3: <c>flow.finished</c> / <c>flow.failed</c> through the real launcher, source and trigger service.</summary>
public sealed class FlowEventTests : IDisposable
{
    private readonly ManualTime _time = new();
    private readonly FakeProvider _provider = new();
    private readonly RunHistory _history = new(null);
    private readonly FlowLauncher _launcher;
    private readonly FlowEventSource _source;
    private readonly Dictionary<string, Flow> _flows = [];
    private readonly List<string> _log = [];
    private readonly object _gate = new();
    private readonly List<(int Target, TaskCompletionSource Done)> _waiters = [];
    private int _completed;

    public FlowEventTests()
    {
        _launcher = new FlowLauncher(new FlowRunner([_provider], _time), _history);
        _source = new FlowEventSource(_launcher);
    }

    private bool _hooked;

    /// <summary>
    /// Counts ended runs. Called after the service loaded the source, so this handler runs after the source's: when a run is
    /// counted, the event it raised has already started (or refused) its followers.
    /// </summary>
    private void Hook()
    {
        if (_hooked)
            return;
        _hooked = true;
        _launcher.RunCompleted += _ =>
        {
            lock (_gate)
            {
                _completed++;
                foreach (var (target, done) in _waiters.Where(w => _completed >= w.Target).ToList())
                    done.TrySetResult();
            }
        };
    }

    public void Dispose() => _time.Dispose();

    private sealed class Flows(FlowEventTests owner) : IFlowStarter
    {
        public CalledFlow Resolve(string flowId) => owner._flows.TryGetValue(flowId, out var flow)
            ? new(flow, Library.Empty, flow.Name, null)
            : new(null, Library.Empty, flowId, "gone", CalledWhy.Missing);

        public bool IsRunning(string flowId) => owner._launcher.IsRunning(flowId);

        public StartAnswer Start(string flowId, string trigger, IReadOnlyList<string> chain, IReadOnlyDictionary<string, string> inputs, string callerName) =>
            throw new NotSupportedException();
    }

    private TriggerService Service() =>
        new([_source], _launcher, new Flows(this), _time, () => false, log: _log.Add);

    /// <summary>A flow whose one step is <paramref name="step"/> ("ok", "fail" or "hang"), started by the given event triggers.</summary>
    private Flow Make(string id, string step, params EventTrigger[] triggers)
    {
        var flow = Flow(Step(step)) with { Id = id, Name = id, Triggers = new([.. triggers]) };
        _flows[id] = flow;
        return flow;
    }

    private static EventTrigger Finished(string? flow = null) => new("flow.finished") { Flow = flow };

    private static EventTrigger Failed(string? flow = null) => new("flow.failed") { Flow = flow };

    private Task Completed(int count)
    {
        lock (_gate)
        {
            if (_completed >= count)
                return Task.CompletedTask;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, done));
            return done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }

    /// <summary>Starts a flow and waits until <paramref name="runs"/> runs have ended in all; then nothing may still be running.</summary>
    private async Task RunAsync(TriggerService service, string id, int runs)
    {
        var all = _flows.Values.Where(f => f.Id != "").ToList();
        service.Apply(all);
        Hook();
        var first = _launcher.TriggerAsync(_flows[id], Library.Empty, "Test");
        await first.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await Completed(runs); // each run starts its followers before it completes, so none is missed
        await service.IdleAsync();
        Assert.Empty(_launcher.RunningFlowIds);
        Assert.Equal(runs, _completed);
    }

    private string[] Ran => [.. _history.Recent.Select(r => r.FlowId)];

    [Fact]
    public async Task AFlowThatEndedStartsTheFlowWaitingForIt()
    {
        Make("src", "ok");
        Make("after", "ok", Finished("src"));
        Make("other", "ok", Finished("nobody"));
        using var service = Service();

        await RunAsync(service, "src", 2);

        Assert.Equal(["src", "after"], Ran);
        Assert.Equal("Event · a flow finished", _history.Recent.First(r => r.FlowId == "after").Trigger);
    }

    [Fact]
    public async Task AFailedRunRaisesFailedNotFinished()
    {
        _provider.On("fail", (_, _) => FakeProvider.Fail("boom"));
        Make("src", "fail");
        Make("onFail", "ok", Failed());
        Make("onDone", "ok", Finished("src"));
        using var service = Service();

        await RunAsync(service, "src", 2);

        Assert.Equal(["src", "onFail"], Ran);
    }

    [Fact]
    public async Task AStoppedRunIsNeitherFinishedNorFailed()
    {
        Make("src", "hang", Finished(), Failed());
        _provider.On("hang", (_, t) => FakeProvider.Hang(t));
        Make("watcher", "ok", Finished(), Failed());
        using var service = Service();
        service.Apply([.. _flows.Values]);

        var run = _launcher.TriggerAsync(_flows["src"], Library.Empty, "Test");
        await _provider.Started("hang");
        Assert.True(_launcher.Cancel("src"));
        await run.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await service.IdleAsync();

        Assert.Equal(["src"], Ran);
        Assert.Equal(FlowOutcome.Cancelled, Assert.Single(_history.Recent).Outcome);
        Assert.Empty(_launcher.RunningFlowIds);
    }

    [Fact]
    public async Task TheFlowFilterIsReadTrimmedAndAnotherFlowDoesNotMatch()
    {
        Make("src", "ok");
        Make("a", "ok", Finished("  src "));
        Make("b", "ok", Finished("src2"));
        using var service = Service();

        await RunAsync(service, "src", 2);

        Assert.Equal(["src", "a"], Ran);
    }

    [Fact]
    public async Task AFlowThatWouldStartItselfStopsWithAPlainMessageInTheHistory()
    {
        Make("loop", "ok", Finished());
        using var service = Service();

        await RunAsync(service, "loop", 1);

        var records = _history.Recent.ToList();
        Assert.Equal(2, records.Count);
        Assert.Equal(FlowOutcome.Cancelled, records[1].Outcome);
        Assert.Contains("loop", records[1].Nodes[0].Error, StringComparison.Ordinal);
        Assert.Contains("again", records[1].Nodes[0].Error, StringComparison.Ordinal);
    }

    [Fact]
    public void AFlowWithNoStepsStillGetsTheNoteInTheHistory()
    {
        var empty = Make("empty", "ok") with { Nodes = [] };

        _launcher.RecordNotStarted(empty, "Event · a flow finished", "Didn't run empty: it would loop.");

        var record = Assert.Single(_history.Recent);
        Assert.Equal(FlowOutcome.Cancelled, record.Outcome);
        Assert.Equal("Didn't run empty: it would loop.", Assert.Single(record.Nodes).Error);
    }

    [Fact]
    public async Task ARingOfFlowsStopsWhereItComesBackToTheStart()
    {
        Make("a", "ok", Finished("b"));
        Make("b", "ok", Finished("a"));
        using var service = Service();

        await RunAsync(service, "a", 2);

        var records = _history.Recent.ToList();
        Assert.Equal(["a", "b", "a"], records.Select(r => r.FlowId));
        Assert.Equal(FlowOutcome.Cancelled, records[2].Outcome);
        Assert.Contains("loop", records[2].Nodes[0].Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ALongChainStopsAtTheDepthLimit()
    {
        Make("f0", "ok");
        for (var i = 1; i <= FlowCalls.MaxDepth + 2; i++)
            Make($"f{i}", "ok", Finished($"f{i - 1}"));
        using var service = Service();

        // f0 .. f7 run (chains of 0..7 flows); f8 would be the ninth.
        await RunAsync(service, "f0", FlowCalls.MaxDepth);

        Assert.Equal(FlowCalls.MaxDepth + 1, _history.Recent.Count);
        var last = _history.Recent[^1];
        Assert.Equal("f8", last.FlowId);
        Assert.Equal(FlowOutcome.Cancelled, last.Outcome);
        Assert.Contains("deep", last.Nodes[0].Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSourceHooksTheLauncherOnlyWhileAFlowSubscribes()
    {
        Make("src", "ok");
        var heard = new List<string>();
        _source.Raised += e => { lock (heard) heard.Add(e.Name); };
        using var service = Service();

        service.Apply([_flows["src"]]); // nobody waits for a flow event
        await _launcher.TriggerAsync(_flows["src"], Library.Empty, "Test");
        lock (heard)
            Assert.Empty(heard);

        service.Apply([Make("w", "ok", Finished("src"))]);
        Hook();
        await _launcher.TriggerAsync(_flows["src"], Library.Empty, "Test");
        await Completed(2); // src, and the flow that was waiting for it
        int before;
        lock (heard)
        {
            Assert.Contains("flow.finished", heard);
            before = heard.Count;
        }

        service.Apply([]);
        await _launcher.TriggerAsync(_flows["src"], Library.Empty, "Test");
        lock (heard)
            Assert.Equal(before, heard.Count);
    }

    [Fact]
    public void AFlowEventCarriesTheIdTheNameAndTheChain()
    {
        var finished = new RunFinished(Guid.NewGuid(), "src", DateTimeOffset.UtcNow, FlowOutcome.PartiallySucceeded) { FlowName = "Source", Chain = ["top"] };

        var e = FlowEventSource.EventFor(finished)!;

        Assert.Equal("flow.finished", e.Name);
        Assert.Equal("src", e.Kind);
        Assert.Equal("Source", e.Detail);
        Assert.Equal(["top", "src"], e.Chain);
        Assert.Equal("flow.failed", FlowEventSource.EventFor(finished with { Outcome = FlowOutcome.Failed })!.Name);
        Assert.Null(FlowEventSource.EventFor(finished with { Outcome = FlowOutcome.Cancelled }));
    }

    [Fact]
    public void AFlowFilterOnAnotherEventOrOnItselfIsSkipped()
    {
        Make("self", "ok", Finished("self"));
        var wrong = Make("wrong", "ok") with { Triggers = new([new EventTrigger("session.lock") { Flow = "src" }]) };
        using var service = Service();

        service.Apply([_flows["self"], wrong]);

        Assert.Equal(0, service.RunningSources);
        Assert.Contains(_log, l => l.Contains("itself", StringComparison.Ordinal));
    }
}
