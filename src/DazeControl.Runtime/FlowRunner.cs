using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Runtime.Providers;
using DazeControl.Runtime.Scheduling;

namespace DazeControl.Runtime;

/// <param name="Outcome">How the run ended.</param>
/// <param name="Nodes">One result per node, in flow order.</param>
public sealed record RunResult(FlowOutcome Outcome, ValueList<NodeResult> Nodes)
{
    /// <summary>Same id as the run's events.</summary>
    public Guid RunId { get; init; }

    /// <summary>Wall-clock start (UTC).</summary>
    public DateTimeOffset Started { get; init; }

    public TimeSpan Duration { get; init; }
}

/// <summary>
/// Runs one flow: the <see cref="Scheduler"/> decides what starts, this class runs those steps on the pool with
/// retries and a timeout each, and cancels what's running when the flow stops or is cancelled. Every task it
/// starts is awaited (ARCHITECTURE → Invariants: no unobserved task).
/// </summary>
public sealed class FlowRunner
{
    private readonly Dictionary<string, IStepProvider> _providers;
    private readonly TimeProvider _time;
    private readonly IReadOnlyList<ProviderDescriptor> _catalog;
    private readonly ResourceLocks _locks;
    private readonly Action<RunEvent>? _events;

    /// <param name="providers">One per step type the engine can run.</param>
    /// <param name="time">Clock for timeouts and retry delays.</param>
    /// <param name="locks">Shared by every runner in the engine, so locks hold across flows.</param>
    /// <param name="catalog">Step types; the compiled-in catalog by default.</param>
    /// <param name="events">Receives <see cref="RunEvent"/>s from pool threads; must be quick and thread-safe.</param>
    public FlowRunner(IEnumerable<IStepProvider> providers, TimeProvider time, ResourceLocks? locks = null,
        IReadOnlyList<ProviderDescriptor>? catalog = null, Action<RunEvent>? events = null)
    {
        _events = events;
        _locks = locks ?? new ResourceLocks();
        _providers = providers.ToDictionary(p => p.Id);
        _time = time;
        _catalog = catalog ?? ProviderCatalog.All;
    }

    /// <param name="flow">The version to run.</param>
    /// <param name="library">The library <paramref name="flow"/> was validated with (<c>FlowState.Library</c>).</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public async Task<RunResult> RunAsync(Flow flow, Library library, CancellationToken cancellationToken)
    {
        var scheduler = new Scheduler(flow);
        var runId = Guid.NewGuid();
        var started = _time.GetUtcNow();
        var startedTicks = _time.GetTimestamp();
        Raise(new RunStarted(runId, flow.Id, started, flow.Nodes.Count) { Flow = flow });
        var reported = new HashSet<string>();
        using var stepsCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = new Dictionary<Task<StepRun>, string>();
        var results = new Dictionary<string, StepRun>();

        void Launch(IEnumerable<string> ids)
        {
            foreach (var id in ids)
                running.Add(RunStepAsync(runId, flow, library, (StepNode)flow.FindNode(id)!, stepsCancel.Token), id);
        }

        NodeResult ResultOf(string id) => results.TryGetValue(id, out var r)
            ? new NodeResult(id, scheduler.Outcomes[id], r.Attempts, r.Error, r.Duration)
            : new NodeResult(id, scheduler.Outcomes[id], Attempts: 0);

        void ReportFinished()
        {
            foreach (var id in scheduler.Outcomes.Keys.Where(reported.Add).ToList())
                Raise(new NodeFinished(runId, flow.Id, _time.GetUtcNow(), ResultOf(id)));
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
            ReportFinished();
            if (scheduler.IsStopped || scheduler.IsCancelled)
                await stepsCancel.CancelAsync().ConfigureAwait(false);
        }
        if (cancellationToken.IsCancellationRequested && !scheduler.IsCancelled)
            scheduler.Cancel(); // cancelled after the last step finished: the outcome stays as run
        ReportFinished(); // nodes resolved without running (empty flow, cancel before start)

        var result = new RunResult(scheduler.Result, new(flow.Nodes.Select(n => ResultOf(n.Id))))
        {
            RunId = runId,
            Started = started,
            Duration = _time.GetElapsedTime(startedTicks),
        };
        Raise(new RunFinished(runId, flow.Id, _time.GetUtcNow(), result.Outcome));
        return result;
    }

    private void Raise(RunEvent e)
    {
        try
        {
            _events?.Invoke(e);
        }
#pragma warning disable CA1031 // fail-soft: a broken listener must not break the run
        catch (Exception)
#pragma warning restore CA1031
        {
        }
    }

    private readonly record struct StepRun(StepResult Result, int Attempts, string? Error, TimeSpan Duration);

    /// <summary>All attempts of one step. Never throws: every failure becomes a <see cref="StepRun"/>.</summary>
    private async Task<StepRun> RunStepAsync(Guid runId, Flow flow, Library library, StepNode node, CancellationToken flowToken)
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
            try
            {
                if (attempt > 1)
                    await Task.Delay(node.Retry!.Delay, _time, flowToken).ConfigureAwait(false);
                flowToken.ThrowIfCancellationRequested(); // a cancelled run starts no further side effects
            }
            catch (OperationCanceledException)
            {
                return new(StepResult.Cancelled, attempt - 1, error, _time.GetElapsedTime(started));
            }
            catch (ArgumentOutOfRangeException e) // a delay the timer can't hold
            {
                return new(StepResult.Failed, attempt - 1, e.Message, _time.GetElapsedTime(started));
            }
            Raise(new NodeStarted(runId, flow.Id, _time.GetUtcNow(), node.Id, attempt));
            error = await AttemptAsync(provider, new StepContext(flow, node, descriptor, library, attempt), timeout, flowToken)
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
        CancellationTokenSource timeoutCancel;
        try
        {
            timeoutCancel = new CancellationTokenSource(timeout, _time);
        }
        catch (ArgumentOutOfRangeException)
        {
            return $"A timeout of {Duration.Format(timeout)} is longer than this version supports.";
        }
        using var ownedTimeout = timeoutCancel;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(flowToken, timeoutCancel.Token);
        IDisposable held;
        try
        {
            // Waiting for a lock counts toward the timeout: a step stuck behind another still ends on time.
            held = await _locks.AcquireAsync(ResourcesOf(context.Descriptor, context.Node, context.Library), linked.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return flowToken.IsCancellationRequested ? "Cancelled." : $"Waited {Duration.Format(timeout)} for another step using the same device.";
        }
        using var release = held;
        if (linked.IsCancellationRequested) // AcquireAsync with no resources doesn't check
            return flowToken.IsCancellationRequested ? "Cancelled." : $"Didn't finish within {Duration.Format(timeout)}.";
        // On the pool, so a provider that blocks before returning its task is still bounded by WaitAsync.
        var execution = Task.Run(() => provider.ExecuteAsync(context, linked.Token), CancellationToken.None);
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

    /// <summary>
    /// The descriptor's lock names with "{param}" replaced by that param's value ("audio"). A Bluetooth or Fire TV
    /// reference becomes the device's address, so two library keys for one device share its lock ("bluetooth:B8:20:…").
    /// </summary>
    internal static IEnumerable<string> ResourcesOf(ProviderDescriptor descriptor, StepNode node, Library library) =>
        descriptor.Resources.Select(resource => node.With.Aggregate(resource, (name, param) =>
            param.Value is ScalarParam scalar
                ? name.Replace("{" + param.Key + "}", Identity(descriptor.FindParam(param.Key), scalar.Text, library), StringComparison.Ordinal)
                : name))
            // Service names are case-insensitive: "Spooler" and "spooler" are one service, so one lock.
            .Select(name => name.StartsWith("service:", StringComparison.Ordinal) ? name.ToUpperInvariant() : name);

    private static string Identity(ParamSpec? spec, string text, Library library) => spec switch
    {
        { Kind: ParamKind.LibraryRef, Library: LibraryKind.Bluetooth }
            when library.Bluetooth.FirstOrDefault(d => d.Key == text) is { } device => device.Address.ToUpperInvariant(),
        { Kind: ParamKind.LibraryRef, Library: LibraryKind.FireTv }
            when library.FireTv.FirstOrDefault(d => d.Key == text) is { } tv => tv.Serial.ToUpperInvariant(),
        _ => text,
    };

    // A provider abandoned on timeout may still fault later; observe it so it never surfaces as unobserved.
    private static void Observe(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
