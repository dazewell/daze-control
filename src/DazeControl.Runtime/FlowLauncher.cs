using DazeControl.Core.Model;
using DazeControl.Core.Runs;

namespace DazeControl.Runtime;

/// <summary>
/// Starts flow runs from triggers (hotkey, tray, CLI) and applies <see cref="Flow.WhenRunning"/> when the flow is
/// already running (FEATURES F1): <c>ignore</c> drops the trigger, <c>queue</c> runs once more afterwards (any
/// number of triggers while running make one queued run), <c>restart</c> cancels the current run and starts
/// over once it has wound down. Different flows run in parallel; the queued or restarted run uses the flow
/// version of the latest trigger.
/// </summary>
/// <param name="runner">The runner for the current configuration (ConfigHost swaps it on reload).</param>
/// <param name="history">Where finished runs are recorded; null records nothing.</param>
public sealed class FlowLauncher(Func<FlowRunner> runner, RunHistory? history = null)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Slot> _slots = [];

    /// <param name="flow">The flow version to run.</param>
    /// <param name="trigger">For the history: "Alt+Z", "Tray", "Command line", "Editor".</param>
    /// <returns>The run's result; null when the trigger was ignored.</returns>
    public Task<RunResult?> TriggerAsync(Flow flow, string trigger = "")
    {
        CancellationTokenSource? toCancel = null;
        Task<RunResult?> task;
        lock (_lock)
        {
            if (!_slots.TryGetValue(flow.Id, out var slot))
                _slots[flow.Id] = slot = new Slot();
            if (slot.Cancel is null)
            {
                task = Start(slot, flow, trigger, completion: null);
            }
            else if (flow.WhenRunning == WhenRunning.Ignore)
            {
                task = Task.FromResult<RunResult?>(null);
            }
            else
            {
                if (flow.WhenRunning == WhenRunning.Restart)
                    toCancel = slot.Cancel;
                slot.PendingFlow = flow;
                slot.PendingTrigger = trigger;
                slot.Pending ??= new TaskCompletionSource<RunResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
                task = slot.Pending.Task;
            }
        }
        toCancel?.Cancel(); // outside the lock: cancellation callbacks run inline
        return task;
    }

    public bool IsRunning(string flowId)
    {
        lock (_lock)
            return _slots.TryGetValue(flowId, out var slot) && slot.Cancel is not null;
    }

    /// <summary>Cancels every run (queued ones are dropped) and waits until all have wound down: engine exit.</summary>
    public async Task CancelAllAsync()
    {
        List<Task> running = [];
        List<CancellationTokenSource> cancels = [];
        lock (_lock)
        {
            foreach (var slot in _slots.Values.Where(s => s.Cancel is not null))
            {
                slot.Pending?.TrySetResult(null);
                slot.Pending = null;
                slot.PendingFlow = null;
                cancels.Add(slot.Cancel!);
                running.Add(slot.Current!);
            }
        }
        foreach (var cancel in cancels)
            await cancel.CancelAsync().ConfigureAwait(false);
        await Task.WhenAll(running).ConfigureAwait(false);
    }

    /// <summary>Under the lock.</summary>
    private Task<RunResult?> Start(Slot slot, Flow flow, string trigger, TaskCompletionSource<RunResult?>? completion)
    {
        var cancel = new CancellationTokenSource();
        slot.Cancel = cancel;
        var current = RunAsync(slot, flow, trigger, cancel, completion);
        slot.Current = current;
        return current;
    }

    private async Task<RunResult?> RunAsync(Slot slot, Flow flow, string trigger, CancellationTokenSource cancel,
        TaskCompletionSource<RunResult?>? completion)
    {
        await Task.Yield(); // the trigger (hotkey on the message thread) returns at once
        RunResult? result = null;
        Exception? failure = null;
        try
        {
            result = await runner().RunAsync(flow, cancel.Token).ConfigureAwait(false);
            history?.Add(new RunRecord(result.RunId, flow.Id, flow.Name, trigger, result.Started, result.Duration,
                result.Outcome, [.. result.Nodes]));
        }
#pragma warning disable CA1031 // fail-soft: a runner bug must not wedge the slot; the caller sees the exception
        catch (Exception e)
#pragma warning restore CA1031
        {
            failure = e;
        }
        lock (_lock)
        {
            cancel.Dispose();
            slot.Cancel = null;
            slot.Current = null;
            if (slot.Pending is { } next)
            {
                slot.Pending = null;
                var nextFlow = slot.PendingFlow!;
                slot.PendingFlow = null;
                Start(slot, nextFlow, slot.PendingTrigger ?? "", next);
            }
        }
        if (failure is not null)
        {
            completion?.TrySetException(failure);
            throw failure;
        }
        completion?.TrySetResult(result);
        return result;
    }

    private sealed class Slot
    {
        public CancellationTokenSource? Cancel { get; set; }

        public Task? Current { get; set; }

        public TaskCompletionSource<RunResult?>? Pending { get; set; }

        public Flow? PendingFlow { get; set; }

        public string? PendingTrigger { get; set; }
    }
}
