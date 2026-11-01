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
/// <param name="runner">Shared by every flow.</param>
/// <param name="history">Where finished runs are recorded; null records nothing.</param>
public sealed class FlowLauncher(FlowRunner runner, RunHistory? history = null)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Slot> _slots = [];
    private bool _closed;

    /// <param name="flow">The flow version to run.</param>
    /// <param name="library">The library that version was validated with (<c>FlowState.Library</c>); a queued
    /// run keeps it even if the config reloads before it starts.</param>
    /// <param name="trigger">For the history: "Alt+Z", "Tray", "Command line", "Editor".</param>
    /// <returns>The run's result; null when the trigger was ignored (or the engine is exiting).</returns>
    public Task<RunResult?> TriggerAsync(Flow flow, Library library, string trigger = "")
    {
        CancellationTokenSource? toCancel = null;
        Task<RunResult?> task;
        var request = new Request(flow, library, trigger);
        lock (_lock)
        {
            if (!_slots.TryGetValue(flow.Id, out var slot))
                _slots[flow.Id] = slot = new Slot();
            if (_closed)
            {
                task = Task.FromResult<RunResult?>(null);
            }
            else if (slot.Cancel is null)
            {
                task = Start(slot, request, completion: null);
            }
            else if (flow.WhenRunning == WhenRunning.Ignore)
            {
                task = Task.FromResult<RunResult?>(null);
            }
            else
            {
                if (flow.WhenRunning == WhenRunning.Restart)
                    toCancel = slot.Cancel;
                slot.PendingRequest = request;
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

    /// <summary>Cancels the flow's run and drops its queued one (tray, <c>dctl cancel</c>); false when it wasn't running.</summary>
    public bool Cancel(string flowId)
    {
        CancellationTokenSource? cancel;
        lock (_lock)
        {
            if (!_slots.TryGetValue(flowId, out var slot) || slot.Cancel is null)
                return false;
            slot.Pending?.TrySetResult(null);
            slot.Pending = null;
            slot.PendingRequest = null;
            cancel = slot.Cancel;
        }
        cancel.Cancel(); // outside the lock: cancellation callbacks run inline
        return true;
    }

    /// <summary>
    /// Engine exit: cancels every run, drops queued ones, ignores later triggers, and waits until all have wound
    /// down.
    /// </summary>
    public async Task CancelAllAsync()
    {
        List<Task> running = [];
        List<CancellationTokenSource> cancels = [];
        lock (_lock)
        {
            _closed = true;
            foreach (var slot in _slots.Values.Where(s => s.Cancel is not null))
            {
                slot.Pending?.TrySetResult(null);
                slot.Pending = null;
                slot.PendingRequest = null;
                cancels.Add(slot.Cancel!);
                running.Add(slot.Current!);
            }
        }
        foreach (var cancel in cancels)
            await cancel.CancelAsync().ConfigureAwait(false);
        try
        {
            await Task.WhenAll(running).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // WhenAll has waited for every run; a failed one was reported to its trigger
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    /// <summary>Under the lock.</summary>
    private Task<RunResult?> Start(Slot slot, Request request, TaskCompletionSource<RunResult?>? completion)
    {
        var cancel = new CancellationTokenSource();
        slot.Cancel = cancel;
        var current = RunAsync(slot, request, cancel, completion);
        slot.Current = current;
        return current;
    }

    private async Task<RunResult?> RunAsync(Slot slot, Request request, CancellationTokenSource cancel,
        TaskCompletionSource<RunResult?>? completion)
    {
        var (flow, library, trigger) = request;
        await Task.Yield(); // the trigger (hotkey on the message thread) returns at once
        RunResult? result = null;
        Exception? failure = null;
        try
        {
            result = await runner.RunAsync(flow, library, cancel.Token).ConfigureAwait(false);
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
            // The source is not disposed: restart and CancelAllAsync cancel it outside the lock, possibly after
            // this point. It has no timer, so nothing leaks.
            slot.Cancel = null;
            slot.Current = null;
            if (slot.Pending is { } next)
            {
                slot.Pending = null;
                var nextRequest = slot.PendingRequest!;
                slot.PendingRequest = null;
                Start(slot, nextRequest, next);
            }
        }
        if (failure is not null)
        {
            if (completion is null)
                throw failure;
            completion.TrySetException(failure); // the caller awaits this one; the internal task stays unfaulted
            return null;
        }
        completion?.TrySetResult(result);
        return result;
    }

    private sealed class Slot
    {
        public CancellationTokenSource? Cancel { get; set; }

        public Task? Current { get; set; }

        public TaskCompletionSource<RunResult?>? Pending { get; set; }

        public Request? PendingRequest { get; set; }
    }

    private sealed record Request(Flow Flow, Library Library, string Trigger);
}
