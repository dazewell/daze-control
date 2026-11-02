using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Runtime.Providers;
using static DazeControl.Runtime.Tests.SchedulerTests;

namespace DazeControl.Runtime.Tests;

/// <summary>
/// The "notify" provider for tests: each step id gets a script (default: succeed at once). Records attempts and
/// signals when a step starts.
/// </summary>
internal sealed class FakeProvider : IStepProvider
{
    private readonly Dictionary<string, Func<StepContext, CancellationToken, Task>> _scripts = [];
    private readonly Dictionary<string, TaskCompletionSource> _started = [];
    private readonly Lock _lock = new();

    public string Id => "notify";

    public List<(string Step, int Attempt)> Attempts { get; } = [];

    public FakeProvider On(string step, Func<StepContext, CancellationToken, Task> script)
    {
        _scripts[step] = script;
        return this;
    }

    public Task Started(string step)
    {
        lock (_lock)
            return Signal(step).Task;
    }

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        lock (_lock)
        {
            Attempts.Add((context.Node.Id, context.Attempt));
            Signal(context.Node.Id).TrySetResult();
        }
        return _scripts.TryGetValue(context.Node.Id, out var script) ? script(context, cancellationToken) : Task.CompletedTask;
    }

    private TaskCompletionSource Signal(string step) =>
        _started.TryGetValue(step, out var tcs) ? tcs : _started[step] = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public static Task Fail(string message) => Task.FromException(new StepFailedException(message));

    public static Task Hang(CancellationToken token) => Task.Delay(Timeout.Infinite, token);
}

public sealed class FlowRunnerTests : IDisposable
{
    private readonly ManualTime _time = new();
    private readonly FakeProvider _provider = new();

    private Task<RunResult> Run(Flow flow) => RunWith(flow, TestContext.Current.CancellationToken);

    private Task<RunResult> RunWith(Flow flow, CancellationToken token) =>
        new FlowRunner([_provider], _time).RunAsync(flow, Library.Empty, token);

    public void Dispose() => _time.Dispose();

    private static NodeResult Node(RunResult result, string id) => result.Nodes.Single(n => n.NodeId == id);

    [Fact]
    public async Task RunStartedCarriesTheVersionThatRuns()
    {
        var events = new List<RunEvent>();
        var flow = Flow(Step("a"));
        await new FlowRunner([_provider], _time, events: e => { lock (events) events.Add(e); })
            .RunAsync(flow, Library.Empty, TestContext.Current.CancellationToken);

        Assert.Same(flow, Assert.Single(events.OfType<RunStarted>()).Flow);
    }

    [Fact]
    public async Task RunsAChainToSuccess()
    {
        var result = await Run(Flow(Step("a"), Step("b", "after: [a]")));

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal([("a", 1), ("b", 1)], _provider.Attempts);
        Assert.All(result.Nodes, n => Assert.Equal(NodeOutcome.Succeeded, n.Outcome));
    }

    [Fact]
    public async Task FailureMessageReachesTheResultAndTheFallbackRuns()
    {
        _provider.On("hp", (_, _) => FakeProvider.Fail("Headphones didn't connect."));

        var result = await Run(Flow(Step("hp"), Step("buds", "after: [{ id: hp, when: failure }]")));

        Assert.Equal(FlowOutcome.PartiallySucceeded, result.Outcome);
        Assert.Equal("Headphones didn't connect.", Node(result, "hp").Error);
        Assert.Equal(NodeOutcome.Succeeded, Node(result, "buds").Outcome);
    }

    [Fact]
    public async Task ProviderThatThrowsSynchronouslyFailsOnlyItsStep()
    {
        _provider.On("a", (_, _) => throw new InvalidOperationException("boom"));

        var result = await Run(Flow(Step("a", "onFailure: ignore"), Step("b", "after: [a]")));

        Assert.Equal("boom", Node(result, "a").Error);
        Assert.Equal(NodeOutcome.Succeeded, Node(result, "b").Outcome);
    }

    [Fact]
    public async Task RetriesAfterTheDelayAndSucceeds()
    {
        _provider.On("a", (context, _) => context.Attempt < 3 ? FakeProvider.Fail("not yet") : Task.CompletedTask);
        var run = Run(Flow(Step("a", "retry: { count: 3, delay: 2s }")));

        for (var i = 0; i < 2; i++)
        {
            await _time.WaitForTimerAsync(); // attempt timeout
            await _time.WaitForTimerAsync(); // retry delay
            Assert.False(run.IsCompleted);
            _time.Advance(TimeSpan.FromSeconds(2));
        }
        var result = await run;

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
        Assert.Equal(3, Node(result, "a").Attempts);
        Assert.Equal(TimeSpan.FromSeconds(4), Node(result, "a").Duration);
    }

    [Fact]
    public async Task GivesUpAfterTheLastRetry()
    {
        _provider.On("a", (_, _) => FakeProvider.Fail("no"));

        var result = await Run(Flow(Step("a", "retry: { count: 2 }")));

        Assert.Equal(FlowOutcome.Failed, result.Outcome);
        Assert.Equal(3, Node(result, "a").Attempts);
        Assert.Equal("no", Node(result, "a").Error);
    }

    [Fact]
    public async Task TimeoutFailsAStepEvenIfTheProviderIgnoresItsToken()
    {
        _provider.On("a", (_, _) => new TaskCompletionSource().Task); // never completes, never cancels
        var run = Run(Flow(Step("a", "timeout: 5s, onFailure: ignore"), Step("b", "after: [a]")));

        await _provider.Started("a");
        await _time.WaitForTimerAsync();
        _time.Advance(TimeSpan.FromSeconds(5));
        var result = await run;

        Assert.Equal(NodeOutcome.FailedIgnored, Node(result, "a").Outcome);
        Assert.Equal("Didn't finish within 5s.", Node(result, "a").Error);
        Assert.Equal(NodeOutcome.Succeeded, Node(result, "b").Outcome);
    }

    [Fact]
    public async Task TimeoutBoundsAProviderThatBlocksBeforeReturningItsTask()
    {
        using var unblock = new ManualResetEventSlim();
        _provider.On("a", (_, _) =>
        {
            unblock.Wait(TestContext.Current.CancellationToken);
            return Task.CompletedTask;
        });
        var run = Run(Flow(Step("a", "timeout: 5s")));

        await _provider.Started("a");
        await _time.WaitForTimerAsync();
        _time.Advance(TimeSpan.FromSeconds(5));
        var result = await run;
        unblock.Set();

        Assert.Equal("Didn't finish within 5s.", Node(result, "a").Error);
    }

    [Fact]
    public async Task CancelledRunStartsNoStep()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();

        var result = await RunWith(Flow(Step("a"), Step("b", "after: [a]")), cancel.Token);

        Assert.Empty(_provider.Attempts);
        Assert.Equal(FlowOutcome.Cancelled, result.Outcome);
        Assert.All(result.Nodes, n => Assert.Equal(0, n.Attempts));
    }

    [Fact]
    public async Task TimeoutTheTimerCantHoldFailsOnlyItsStep()
    {
        var flow = Flow(Step("a", "onFailure: ignore"), Step("b", "after: [a]")); // validation would reject 1200h
        flow = flow with { Nodes = [((StepNode)flow.Nodes[0]) with { Timeout = TimeSpan.FromHours(1200) }, flow.Nodes[1]] };

        var result = await Run(flow);

        Assert.Contains("longer than", Node(result, "a").Error, StringComparison.Ordinal);
        Assert.DoesNotContain(_provider.Attempts, a => a.Step == "a");
        Assert.Equal(NodeOutcome.Succeeded, Node(result, "b").Outcome);
    }

    [Fact]
    public async Task UsesTheDescriptorTimeoutWhenTheNodeHasNone()
    {
        _provider.On("a", (_, token) => FakeProvider.Hang(token));
        var run = Run(Flow(Step("a")));

        await _provider.Started("a");
        await _time.WaitForTimerAsync();
        _time.Advance(TimeSpan.FromSeconds(9));
        Assert.False(run.IsCompleted);
        _time.Advance(TimeSpan.FromSeconds(1)); // notify's default is 10 s
        var result = await run;

        Assert.Equal("Didn't finish within 10s.", Node(result, "a").Error);
    }

    [Fact]
    public async Task StoppedFlowCancelsRunningSteps()
    {
        var release = new TaskCompletionSource();
        _provider.On("slow", (_, token) => FakeProvider.Hang(token));
        _provider.On("a", async (_, _) =>
        {
            await release.Task;
            throw new StepFailedException("a failed");
        });
        var run = Run(Flow(Step("a"), Step("slow"), Step("b", "after: [a]")));

        await _provider.Started("slow");
        release.SetResult();
        var result = await run;

        Assert.Equal(FlowOutcome.Failed, result.Outcome);
        Assert.Equal(NodeOutcome.Cancelled, Node(result, "slow").Outcome);
        Assert.Equal(NodeOutcome.Cancelled, Node(result, "b").Outcome);
        Assert.Equal(0, Node(result, "b").Attempts);
    }

    [Fact]
    public async Task CancellingTheRunCancelsEverything()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _provider.On("a", (_, token) => FakeProvider.Hang(token));
        var run = RunWith(Flow(Step("a"), Step("b", "after: [a]")), cancel.Token);

        await _provider.Started("a");
        await cancel.CancelAsync();
        var result = await run;

        Assert.Equal(FlowOutcome.Cancelled, result.Outcome);
        Assert.All(result.Nodes, n => Assert.Equal(NodeOutcome.Cancelled, n.Outcome));
    }

    [Fact]
    public async Task CardMembersRunConcurrently()
    {
        var bothStarted = new TaskCompletionSource();
        _provider.On("a", async (_, _) => await bothStarted.Task);
        _provider.On("b", (_, _) =>
        {
            bothStarted.SetResult(); // only reachable if b starts while a is still waiting
            return Task.CompletedTask;
        });

        var result = await Run(Flow(Step("a"), Step("b"), "{ id: g, group: [a, b] }"));

        Assert.Equal(FlowOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task UnknownStepTypeFailsItsStep()
    {
        var result = await new FlowRunner([], _time).RunAsync(Flow(Step("a", "onFailure: ignore")), Library.Empty, TestContext.Current.CancellationToken);

        Assert.Contains("can't run \"notify\"", Node(result, "a").Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResultsCarryStartOrderStartAndRetryStartsForTheTimeline()
    {
        var gate = new TaskCompletionSource();
        _provider.On("a", async (_, _) => await gate.Task);
        _provider.On("c", (context, _) => context.Attempt < 2 ? FakeProvider.Fail("not yet") : Task.CompletedTask);
        var run = Run(Flow(Step("a"), Step("b", "after: [a]"), Step("c", "after: [b], retry: { count: 1, delay: 2s }"),
            Step("never", "after: [{ id: a, when: failure }]")));

        await _provider.Started("a");
        _time.Advance(TimeSpan.FromSeconds(3));
        gate.SetResult();
        await _provider.Started("c");
        await _time.WaitForTimerAsync(); // c's attempt timeout
        await _time.WaitForTimerAsync(); // retry delay
        _time.Advance(TimeSpan.FromSeconds(2));
        var result = await run;

        Assert.Equal([1, 2, 3, 0], result.Nodes.Select(n => n.Order));
        Assert.Equal(TimeSpan.Zero, Node(result, "a").Start);
        Assert.Equal(TimeSpan.FromSeconds(3), Node(result, "b").Start);
        Assert.Equal(TimeSpan.FromSeconds(3), Node(result, "c").Start);
        Assert.Equal([TimeSpan.FromSeconds(5)], Node(result, "c").Retries!);
        Assert.Null(Node(result, "b").Retries);
    }
}
