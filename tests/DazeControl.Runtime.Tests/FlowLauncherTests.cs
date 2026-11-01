using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using static DazeControl.Runtime.Tests.SchedulerTests;

namespace DazeControl.Runtime.Tests;

public sealed class FlowLauncherTests : IDisposable
{
    private readonly ManualTime _time = new();
    private readonly FakeProvider _provider = new();
    private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly FlowLauncher _launcher;

    public FlowLauncherTests()
    {
        // Every run of step "a" waits for the gate (or its cancellation); later runs pass straight through.
        _provider.On("a", (_, token) => _gate.Task.WaitAsync(token));
        _launcher = new FlowLauncher(new FlowRunner([_provider], _time));
    }

    public void Dispose() => _time.Dispose();

    private static Flow Gated(WhenRunning whenRunning) => Flow(Step("a")) with { WhenRunning = whenRunning };

    private int Runs => _provider.Attempts.Count;

    [Fact]
    public async Task IgnoreDropsTriggersWhileRunning()
    {
        var first = _launcher.TriggerAsync(Gated(WhenRunning.Ignore), Library.Empty);
        await _provider.Started("a");

        Assert.Null(await _launcher.TriggerAsync(Gated(WhenRunning.Ignore), Library.Empty));
        Assert.True(_launcher.IsRunning("f"));
        _gate.SetResult();

        Assert.Equal(FlowOutcome.Succeeded, (await first)!.Outcome);
        Assert.Equal(1, Runs);
        Assert.False(_launcher.IsRunning("f"));
    }

    [Fact]
    public async Task QueueRunsOnceMoreAfterTheCurrentRun()
    {
        var first = _launcher.TriggerAsync(Gated(WhenRunning.Queue), Library.Empty);
        await _provider.Started("a");

        var second = _launcher.TriggerAsync(Gated(WhenRunning.Queue), Library.Empty);
        var third = _launcher.TriggerAsync(Gated(WhenRunning.Queue), Library.Empty);
        Assert.Same(second, third);
        _gate.SetResult();

        Assert.Equal(FlowOutcome.Succeeded, (await first)!.Outcome);
        Assert.Equal(FlowOutcome.Succeeded, (await second)!.Outcome);
        Assert.Equal(2, Runs);
    }

    [Fact]
    public async Task RestartCancelsTheCurrentRunAndStartsOver()
    {
        var first = _launcher.TriggerAsync(Gated(WhenRunning.Restart), Library.Empty);
        await _provider.Started("a");

        var second = _launcher.TriggerAsync(Gated(WhenRunning.Restart), Library.Empty);
        Assert.Equal(FlowOutcome.Cancelled, (await first)!.Outcome);
        _gate.SetResult();

        Assert.Equal(FlowOutcome.Succeeded, (await second)!.Outcome);
        Assert.Equal(2, Runs);
    }

    [Fact]
    public async Task FinishedFlowRunsAgainOnTheNextTrigger()
    {
        _gate.SetResult();

        await _launcher.TriggerAsync(Gated(WhenRunning.Ignore), Library.Empty);
        var again = await _launcher.TriggerAsync(Gated(WhenRunning.Ignore), Library.Empty);

        Assert.NotNull(again);
        Assert.Equal(2, Runs);
    }

    [Fact]
    public async Task DifferentFlowsRunInParallel()
    {
        var first = _launcher.TriggerAsync(Gated(WhenRunning.Ignore), Library.Empty);
        await _provider.Started("a");

        var other = await _launcher.TriggerAsync(Flow(Step("b")) with { Id = "g" }, Library.Empty);

        Assert.Equal(FlowOutcome.Succeeded, other!.Outcome);
        _gate.SetResult();
        await first;
    }

    [Fact]
    public async Task CancelAllEndsRunsAndDropsQueuedOnes()
    {
        var first = _launcher.TriggerAsync(Gated(WhenRunning.Queue), Library.Empty);
        await _provider.Started("a");
        var queued = _launcher.TriggerAsync(Gated(WhenRunning.Queue), Library.Empty);

        await _launcher.CancelAllAsync();

        Assert.Equal(FlowOutcome.Cancelled, (await first)!.Outcome);
        Assert.Null(await queued);
        Assert.Equal(1, Runs);
    }

    [Fact]
    public async Task TriggersDuringAndAfterExitAreIgnored()
    {
        var first = _launcher.TriggerAsync(Gated(WhenRunning.Queue), Library.Empty);
        await _provider.Started("a");

        var exit = _launcher.CancelAllAsync();
        var during = _launcher.TriggerAsync(Gated(WhenRunning.Queue), Library.Empty);
        await exit;

        Assert.Null(await during);
        Assert.Null(await _launcher.TriggerAsync(Gated(WhenRunning.Queue), Library.Empty));
        Assert.Equal(FlowOutcome.Cancelled, (await first)!.Outcome);
        Assert.Equal(1, Runs);
    }

    [Fact]
    public async Task QueuedRunKeepsTheLibraryOfItsTrigger()
    {
        var seen = new List<Library>();
        _provider.On("a", (context, token) =>
        {
            lock (seen)
                seen.Add(context.Library);
            return _gate.Task.WaitAsync(token);
        });
        var newer = new Library { Bluetooth = [new BluetoothDevice("hp", "B8:20:8E:ED:48:FC")] };
        var first = _launcher.TriggerAsync(Gated(WhenRunning.Queue), Library.Empty);
        await _provider.Started("a");

        var queued = _launcher.TriggerAsync(Gated(WhenRunning.Queue), newer);
        _gate.SetResult();
        await first;
        await queued;

        Assert.Equal([Library.Empty, newer], seen);
    }
}
