using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Runtime.Providers;
using DazeControl.Runtime.Scheduling;

namespace DazeControl.Runtime;

/// <param name="Outcome">How the run ended.</param>
/// <param name="Nodes">One result per node, in flow order.</param>
public sealed record RunResult(FlowOutcome Outcome, ValueList<NodeResult> Nodes);

/// <summary>
/// Runs one flow: the <see cref="Scheduler"/> decides what starts, this class runs those steps on the pool with
/// retries and a timeout each, and cancels what's running when the flow stops or is cancelled. Every task it
/// starts is awaited (ARCHITECTURE → Invariants: no unobserved task).
/// </summary>
public sealed class FlowRunner
{
    private readonly Dictionary<string, IStepProvider> _providers;
    private readonly Library _library;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<ProviderDescriptor> _catalog;

    public FlowRunner(IEnumerable<IStepProvider> providers, Library library, TimeProvider time,
        IReadOnlyList<ProviderDescriptor>? catalog = null)
    {
        _providers = providers.ToDictionary(p => p.Id);
        _library = library;
        _time = time;
        _catalog = catalog ?? ProviderCatalog.All;
    }

    public async Task<RunResult> RunAsync(Flow flow, CancellationToken cancellationToken)
    {
        var scheduler = new Scheduler(flow);
        using var stepsCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = new Dictionary<Task<StepRun>, string>();
        var results = new Dictionary<string, StepRun>();

        void Launch(IEnumerable<string> ids)
        {
            foreach (var id in ids)
                running.Add(RunStepAsync(flow, (StepNode)flow.FindNode(id)!, stepsCancel.Token), id);
        }

        Launch(scheduler.Start());
        while (running.Count > 0)
        {
            var done = await Task.WhenAny(running.Keys).ConfigureAwait(false);
            var id = running[done];
            running.Remove(done);
            var run = await done.ConfigureAwait(false); // RunStepAsync never throws
            results[id] = run;
            if (cancellationToken.IsCancellationRequested && !scheduler.IsCancelled)
                scheduler.Cancel();
            Launch(scheduler.Complete(id, run.Result));
            if (scheduler.IsStopped || scheduler.IsCancelled)
                await stepsCancel.CancelAsync().ConfigureAwait(false);
        }
        if (cancellationToken.IsCancellationRequested && !scheduler.IsCancelled)
            scheduler.Cancel(); // cancelled after the last step finished: the outcome stays as run

        var nodes = flow.Nodes.Select(n => results.TryGetValue(n.Id, out var r)
            ? new NodeResult(n.Id, scheduler.Outcomes[n.Id], r.Attempts, r.Error, r.Duration)
            : new NodeResult(n.Id, scheduler.Outcomes[n.Id], Attempts: 0));
        return new RunResult(scheduler.Result, new(nodes));
    }

    private readonly record struct StepRun(StepResult Result, int Attempts, string? Error, TimeSpan Duration);

    /// <summary>All attempts of one step. Never throws: every failure becomes a <see cref="StepRun"/>.</summary>
    private async Task<StepRun> RunStepAsync(Flow flow, StepNode node, CancellationToken flowToken)
    {
        await Task.Yield(); // run on the pool, never inline in the scheduler loop
        var started = _time.GetTimestamp();
        var descriptor = _catalog.FirstOrDefault(d => d.Id == node.Step);
        if (descriptor is null || !_providers.TryGetValue(node.Step, out var provider))
            return new(StepResult.Failed, 1, $"This version can't run \"{node.Step}\" steps.", TimeSpan.Zero);

        var timeout = node.Timeout ?? descriptor.DefaultTimeout;
        var attempts = 1 + Math.Max(0, node.Retry?.Count ?? 0);
        string? error = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            if (attempt > 1)
            {
                try
                {
                    await Task.Delay(node.Retry!.Delay, _time, flowToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return new(StepResult.Cancelled, attempt - 1, error, _time.GetElapsedTime(started));
                }
            }
            error = await AttemptAsync(provider, new StepContext(flow, node, descriptor, _library, attempt), timeout, flowToken)
                .ConfigureAwait(false);
            if (error is null)
                return new(StepResult.Succeeded, attempt, null, _time.GetElapsedTime(started));
            if (flowToken.IsCancellationRequested)
                return new(StepResult.Cancelled, attempt, error, _time.GetElapsedTime(started));
        }
        return new(StepResult.Failed, attempts, error, _time.GetElapsedTime(started));
    }

    /// <returns>Null on success, else why it failed.</returns>
    private async Task<string?> AttemptAsync(IStepProvider provider, StepContext context, TimeSpan timeout, CancellationToken flowToken)
    {
        using var timeoutCancel = new CancellationTokenSource(timeout, _time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(flowToken, timeoutCancel.Token);
        Task execution;
        try
        {
            execution = provider.ExecuteAsync(context, linked.Token);
        }
#pragma warning disable CA1031 // fail-soft: a provider bug fails its step, never the engine
        catch (Exception e)
#pragma warning restore CA1031
        {
            return e.Message;
        }
        try
        {
            // WaitAsync ends the attempt on time even if the provider ignores its token.
            await execution.WaitAsync(linked.Token).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            Observe(execution);
            return flowToken.IsCancellationRequested ? "Cancelled." : $"Didn't finish within {Duration.Format(timeout)}.";
        }
#pragma warning disable CA1031
        catch (Exception e)
#pragma warning restore CA1031
        {
            return e.Message;
        }
    }

    // A provider abandoned on timeout may still fault later; observe it so it never surfaces as unobserved.
    private static void Observe(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
