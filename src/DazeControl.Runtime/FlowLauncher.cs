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

    /// <summary>
    /// A run ended, after its history record was written and its slot freed (a subscriber reacting at once sees
    /// both). From a pool thread; quick. A handler that throws goes to <see cref="ListenerFailed"/>.
    /// </summary>
    public event Action<RunFinished>? RunCompleted;

    public event Action<Exception>? ListenerFailed;

    /// <summary>Asks <c>ask: true</c> inputs before a run starts (D-107); null = they take their defaults (tests, no window).</summary>
    public Menus.InputPrompts? StartPrompts { get; set; }

    /// <param name="flow">The flow version to run.</param>
    /// <param name="library">The library that version was validated with (<c>FlowState.Library</c>); a queued
    /// run keeps it even if the config reloads before it starts.</param>
    /// <param name="trigger">For the history: "Alt+Z", "Tray", "Command line", "Editor".</param>
    /// <param name="chain">A start-mode <c>flow.run</c>'s call chain (D-063); null for a trigger by a person.</param>
    /// <param name="eventDetail">The device or network name an event brought, for <c>{@event}</c> (T24.6).</param>
    /// <returns>The run's result; null when the trigger was ignored (or the engine is exiting).</returns>
    /// <param name="inputs">Values from the command line (T24.9), already checked: device inputs are already filled in
    /// <paramref name="flow"/>; text inputs are read from here. Null = defaults.</param>
    /// <param name="startAt">"Run from this step" (T27.4, D-148): a step of <paramref name="flow"/>; null = the whole flow.</param>
    public Task<RunResult?> TriggerAsync(Flow flow, Library library, string trigger = "", IReadOnlyList<string>? chain = null,
        IReadOnlyDictionary<string, string>? inputs = null, string eventDetail = "", string? startAt = null)
    {
        CancellationTokenSource? toCancel = null;
        Task<RunResult?> task;
        var request = new Request(flow, library, trigger, chain ?? [], inputs, eventDetail, startAt);
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

    /// <summary>
    /// Writes "it didn't start" into the history as a quiet, stopped run with <paramref name="note"/> on its first step: for a
    /// start a guard refused (a flow event that would loop, T26.3), so the person can see why nothing ran. No events, no pop-up.
    /// </summary>
    public void RecordNotStarted(Flow flow, string trigger, string note)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var result = NotStarted(flow, note);
        history?.Add(new RunRecord(result.RunId, flow.Id, flow.Name, trigger, result.Started, TimeSpan.Zero, result.Outcome, [.. result.Nodes]));
    }

    public bool IsRunning(string flowId)
    {
        lock (_lock)
            return _slots.TryGetValue(flowId, out var slot) && slot.Cancel is not null;
    }

    /// <summary>Flows with a run now, whether or not they are still in the config.</summary>
    public IReadOnlyList<string> RunningFlowIds
    {
        get
        {
            lock (_lock)
                return [.. _slots.Where(s => s.Value.Cancel is not null).Select(s => s.Key)];
        }
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
    /// <param name="after">Completes once the previous run of this flow has published <see cref="RunCompleted"/>, so
    /// a flow's completions arrive in run order.</param>
    private Task<RunResult?> Start(Slot slot, Request request, TaskCompletionSource<RunResult?>? completion, Task? after = null)
    {
        var cancel = new CancellationTokenSource();
        slot.Cancel = cancel;
        var current = RunAsync(slot, request, cancel, completion, after);
        slot.Current = current;
        return current;
    }

    private async Task<RunResult?> RunAsync(Slot slot, Request request, CancellationTokenSource cancel,
        TaskCompletionSource<RunResult?>? completion, Task? after)
    {
        var (flow, library, trigger, chain, inputs, eventDetail, startAt) = request;
        await Task.Yield(); // the trigger (hotkey on the message thread) returns at once
        if (after is not null)
            await after.ConfigureAwait(false);
        RunResult? result = null;
        Exception? failure = null;
        try
        {
            string? note = null;
            if (StartPrompts is { } prompts && flow.Inputs.Values.Any(i => i.Ask))
            {
                var answer = await prompts.AskAsync(flow, library, trigger, cancel.Token).ConfigureAwait(false);
                note = answer.Note;
                flow = answer.Flow ?? flow;
            }
            else
            {
                flow = FlowInputs.Apply(flow, new Dictionary<string, string>()); // an asked input nobody can ask takes its default
            }
            result = note is not null ? NotStarted(flow, note) : await runner.RunAsync(flow, library, chain, cancel.Token, trigger, inputs, eventDetail, startAt).ConfigureAwait(false);
            history?.Add(new RunRecord(result.RunId, flow.Id, flow.Name, trigger, result.Started, result.Duration,
                result.Outcome, [.. result.Nodes]));
        }
#pragma warning disable CA1031 // fail-soft: a runner bug must not wedge the slot; the caller sees the exception
        catch (Exception e)
#pragma warning restore CA1031
        {
            failure = e;
        }
        TaskCompletionSource? published = null;
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
                published = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Start(slot, nextRequest, next, published.Task);
            }
        }
        if (result is not null)
            RaiseCompleted(new RunFinished(result.RunId, flow.Id, result.Started + result.Duration, result.Outcome) { Quiet = result.Quiet, FlowName = flow.Name, Chain = chain });
        published?.SetResult();
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

    /// <summary>A run that ended before its first step because a prompt got no answer: quiet, the note on the first step for history.</summary>
    private static RunResult NotStarted(Flow flow, string note) =>
        new(FlowOutcome.Cancelled, new(flow.Nodes.Count == 0
            // A flow with no steps (the validator allows it) has no first step to carry the note: a stand-in keeps it in Runs.
            ? [new NodeResult("(not started)", NodeOutcome.Cancelled, Attempts: 0, Error: note)]
            : flow.Nodes.Select((n, i) => new NodeResult(n.Id, NodeOutcome.Cancelled, Attempts: 0, Error: i == 0 ? note : null))))
        {
            RunId = Guid.NewGuid(),
            Started = DateTimeOffset.UtcNow,
            Quiet = true,
        };

    private void RaiseCompleted(RunFinished finished)
    {
        try
        {
            RunCompleted?.Invoke(finished);
        }
#pragma warning disable CA1031 // fail-soft: a listener bug must not lose the run's result
        catch (Exception e)
#pragma warning restore CA1031
        {
            ListenerFailed?.Invoke(e);
        }
    }

    private sealed class Slot
    {
        public CancellationTokenSource? Cancel { get; set; }

        public Task? Current { get; set; }

        public TaskCompletionSource<RunResult?>? Pending { get; set; }

        public Request? PendingRequest { get; set; }
    }

    private sealed record Request(Flow Flow, Library Library, string Trigger, IReadOnlyList<string> Chain, IReadOnlyDictionary<string, string>? Inputs, string EventDetail = "", string? StartAt = null);
}
