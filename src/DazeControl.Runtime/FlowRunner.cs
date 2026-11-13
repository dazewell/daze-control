using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Validation;
using DazeControl.Runtime.Control;
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

    /// <summary>A step ended the run on purpose (<see cref="StepEndsFlowException"/>): the pop-up shows nothing.</summary>
    public bool Quiet { get; init; }

    /// <summary>The run ended quietly only because a Stop flow step said so (T24.1): a caller that waited counts its step as done.</summary>
    public bool Stopped { get; init; }

    /// <summary>The flow's outputs (T23.7), filled when it succeeded (fully or partly); empty otherwise.</summary>
    public ValueDictionary<string> Outputs { get; init; }

    /// <summary>Outputs that hold a Sensitive value of the run (a step's, or a text input the caller filled from one).</summary>
    public ValueList<string> SensitiveOutputs { get; init; } = [];
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
    private readonly Dictionary<string, IFactProbe> _probes;

    /// <param name="providers">One per step type the engine can run.</param>
    /// <param name="time">Clock for timeouts and retry delays.</param>
    /// <param name="locks">Shared by every runner in the engine, so locks hold across flows.</param>
    /// <param name="catalog">Step types; the compiled-in catalog by default.</param>
    /// <param name="events">Receives <see cref="RunEvent"/>s from pool threads; must be quick and thread-safe.</param>
    /// <param name="probes">Read-only fact probes for "Only if" (D-081). None (the dry runner) = every condition counts as met.</param>
    public FlowRunner(IEnumerable<IStepProvider> providers, TimeProvider time, ResourceLocks? locks = null,
        IReadOnlyList<ProviderDescriptor>? catalog = null, Action<RunEvent>? events = null, IEnumerable<IFactProbe>? probes = null)
    {
        _probes = (probes ?? []).ToDictionary(p => p.Fact);
        _events = events;
        _locks = locks ?? new ResourceLocks();
        _providers = providers.ToDictionary(p => p.Id);
        _time = time;
        _catalog = catalog ?? ProviderCatalog.All;
    }

    /// <summary>
    /// Where <c>flow.run</c> steps that wait find the flows they run (D-062). Null = such steps go to the
    /// <c>flow.run</c> provider like any step (the dry runner: a call doesn't descend, D-042).
    /// </summary>
    public IFlowStarter? Flows { get; set; }

    /// <param name="flow">The version to run.</param>
    /// <param name="library">The library <paramref name="flow"/> was validated with (<c>FlowState.Library</c>).</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    public Task<RunResult> RunAsync(Flow flow, Library library, CancellationToken cancellationToken) =>
        RunAsync(flow, library, [], cancellationToken);

    /// <param name="flow">The version to run.</param>
    /// <param name="library">The library <paramref name="flow"/> was validated with.</param>
    /// <param name="chain">The call chain of the flow that started this one (start-mode <c>flow.run</c>); empty for
    /// a trigger by a person, which starts a fresh chain (D-063).</param>
    /// <param name="cancellationToken">Cancels the run.</param>
    /// <param name="trigger">What started the run ("Schedule · 22:30"): prompts there default to stopping (D-107).</param>
    public Task<RunResult> RunAsync(Flow flow, Library library, IReadOnlyList<string> chain, CancellationToken cancellationToken, string trigger = "")
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(chain);
        return RunCoreAsync(flow, library, NoInputs, [.. chain, flow.Id], null, null, cancellationToken, trigger);
    }

    /// <summary>
    /// A flow run inside a <c>flow.run</c> step (D-062): its own scheduler and RunId, events tagged with the parent,
    /// no launcher slot (<c>whenRunning</c> doesn't apply). Locks are taken by its steps, as at top level.
    /// </summary>
    /// <param name="flow">The called flow's runnable version.</param>
    /// <param name="library">The library that version was validated with.</param>
    /// <param name="chain">Flow ids from the top run down to the caller; <paramref name="flow"/> is appended.</param>
    /// <param name="parentRunId">The calling run.</param>
    /// <param name="parentNodeId">The calling step.</param>
    /// <param name="inputs">What the caller passes (D-064): input name → Library key; missing inputs use their defaults.</param>
    /// <param name="cancellationToken">The calling step's: the caller's cancel, stop or timeout ends this run.</param>
    public Task<RunResult> RunNestedAsync(Flow flow, Library library, IReadOnlyList<string> chain, Guid parentRunId,
        string parentNodeId, IReadOnlyDictionary<string, string> inputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(inputs);
        return RunCoreAsync(flow, library, inputs, [.. chain, flow.Id], parentRunId, parentNodeId, cancellationToken);
    }

    private static readonly IReadOnlyDictionary<string, string> NoInputs = new Dictionary<string, string>();

    /// <summary>Finds a called flow for its outputs (T23.7); null when calls don't descend (the dry runner).</summary>
    private Func<string, Flow?>? CalledFlows => Flows is null ? null : id => Flows.Resolve(id).Flow;

    /// <param name="sensitiveInputs">Text inputs the caller filled from a Sensitive value (T23.7): hidden in errors and summaries.</param>
    private async Task<RunResult> RunCoreAsync(Flow flow, Library library, IReadOnlyDictionary<string, string> inputs, string[] chain,
        Guid? parentRunId, string? parentNodeId, CancellationToken cancellationToken, string trigger = "", HashSet<string>? sensitiveInputs = null)
    {
        // Inputs resolve before anything runs (D-064): steps, their locks and nested calls only ever see Library keys.
        flow = FlowInputs.Apply(flow, inputs);
        var scheduler = new Scheduler(flow);
        var runId = Guid.NewGuid();
        var started = _time.GetUtcNow();
        var startedTicks = _time.GetTimestamp();
        var scope = new RunScope(runId, chain, parentRunId, parentNodeId, trigger);
        Raise(new RunStarted(runId, flow.Id, started, flow.Nodes.Count) { Flow = flow, ParentRunId = parentRunId, ParentNodeId = parentNodeId });
        var reported = new HashSet<string>();
        using var stepsCancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var running = new Dictionary<Task<StepRun>, string>();
        var results = new Dictionary<string, StepRun>();
        var order = new Dictionary<string, int>();
        var quiet = false;
        var otherQuiet = false;
        // D-117, D-129: what steps give, only what some setting references; thread-safe for parallel branches.
        var calls = CalledFlows;
        var values = new RunValues(StepValues.Wanted(flow, _catalog, calls), _time) { FlowName = flow.Name, Trigger = trigger };
        foreach (var (name, input) in flow.Inputs)
        {
            var given = inputs.TryGetValue(name, out var key) ? key : input.Default;
            if (input.Kind is { } kind)
                values.SetInput(name, kind, given.Trim());
            else
                values.SetInputText(name, given, sensitiveInputs?.Contains(name) == true); // T23.7: as the caller filled it
        }

        void Launch(IEnumerable<string> ids)
        {
            foreach (var id in ids)
            {
                // Numbered here, on the scheduler's thread, so parallel steps get a stable start order (dry run).
                order[id] = order.Count + 1;
                running.Add(RunStepAsync(scope, flow, library, (StepNode)flow.FindNode(id)!, values, startedTicks, stepsCancel.Token), id);
            }
        }

        NodeResult ResultOf(string id) => results.TryGetValue(id, out var r)
            ? new NodeResult(id, scheduler.Outcomes[id], r.Attempts, r.Error, r.Duration, r.Start, order[id],
                r.Retries.Count > 0 ? [.. r.Retries] : null) { Called = r.Called, Summary = r.Summary, SkippedBecause = r.SkippedBecause }
            : new NodeResult(id, scheduler.Outcomes[id], Attempts: 0);

        void ReportFinished()
        {
            foreach (var id in scheduler.Outcomes.Keys.Where(reported.Add).ToList())
                Raise(new NodeFinished(runId, flow.Id, _time.GetUtcNow(), ResultOf(id)) { ParentRunId = parentRunId, ParentNodeId = parentNodeId });
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
            if (run.Result == StepResult.Succeeded && run.Called?.Outcome == FlowOutcome.PartiallySucceeded)
                scheduler.MarkPartialCall();
            if (run.Quiet) // before Complete: a stop that finishes a parallel card must not start the steps after it
            {
                quiet = true; // a step ended the run on purpose, or the flow it called did (a menu, nothing picked)
                otherQuiet |= !run.Stopped;
                if (!scheduler.IsCancelled)
                    scheduler.Cancel();
            }
            var next = scheduler.Complete(id, run.Result).ToList();
            ReportFinished(); // before the next steps start, so a listener sees a step finish before its dependents begin
            Launch(next);
            ReportFinished();
            if (scheduler.IsStopped || scheduler.IsCancelled)
                await stepsCancel.CancelAsync().ConfigureAwait(false);
        }
        if (cancellationToken.IsCancellationRequested && !scheduler.IsCancelled)
            scheduler.Cancel(); // cancelled after the last step finished: the outcome stays as run
        ReportFinished(); // nodes resolved without running (empty flow, cancel before start)

        // Outputs (T23.7) are filled once, from what the steps gave, when the run has worked.
        var outputs = flow.Outputs.Count == 0 || scheduler.Result is not (FlowOutcome.Succeeded or FlowOutcome.PartiallySucceeded) ? default
            : DryRun ? new ValueDictionary<string>(flow.Outputs.Select(o => KeyValuePair.Create(o.Key, StepValues.Describe(flow, o.Value, _catalog, flows: calls))))
            : new ValueDictionary<string>(FlowOutputs.Fill(flow, values, library));
        var endSecrets = outputs.Count == 0 || DryRun ? [] : StepValues.Secrets(flow, null, values, library, _catalog, calls);
        ValueList<string> sensitiveOutputs = [.. outputs.Where(o => endSecrets.Any(s => o.Value.Contains(s.Text, StringComparison.Ordinal))).Select(o => o.Key)];
        var result = new RunResult(scheduler.Result, new(flow.Nodes.Select(n => ResultOf(n.Id))))
        {
            RunId = runId,
            Started = started,
            Duration = _time.GetElapsedTime(startedTicks),
            Quiet = quiet,
            Stopped = quiet && !otherQuiet,
            Outputs = outputs,
            SensitiveOutputs = sensitiveOutputs,
        };
        Raise(new RunFinished(runId, flow.Id, _time.GetUtcNow(), result.Outcome) { ParentRunId = parentRunId, ParentNodeId = parentNodeId, Quiet = quiet });
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

    /// <param name="Start">From the run's start.</param>
    /// <param name="Retries">Start of each try after the first, from the run's start.</param>
    /// <param name="Called">A call that waited: its last try's nested run.</param>
    /// <param name="Summary">What the step said about itself (<see cref="StepContext.SetSummary"/>), last try's.</param>
    /// <param name="Quiet">The step ended the run on purpose, or the called flow ended quietly: this run ends quietly too.</param>
    /// <param name="SkippedBecause">"Only if" wasn't met: the probe's sentence (D-081). The result is Succeeded.</param>
    private readonly record struct StepRun(StepResult Result, int Attempts, string? Error, TimeSpan Duration, TimeSpan Start,
        List<TimeSpan> Retries, CalledRun? Called = null, bool Quiet = false, string? Summary = null, string? SkippedBecause = null, bool Stopped = false);

    /// <summary>One try of an ordinary step: null <paramref name="Error"/> = it worked; <paramref name="EndsFlow"/> = see <see cref="StepEndsFlowException"/>.</summary>
    private readonly record struct AttemptResult(string? Error, bool EndsFlow = false, string? SkippedBecause = null, bool Stop = false);

    /// <param name="Chain">Flow ids from the top run down to this run's flow.</param>
    private sealed record RunScope(Guid RunId, string[] Chain, Guid? ParentRunId, string? ParentNodeId, string Trigger = "");

    /// <summary>One try of a call that waits.</summary>
    private readonly record struct CallTry(string? Error, CalledRun? Called, bool Quiet);

    /// <summary>All attempts of one step. Never throws: every failure becomes a <see cref="StepRun"/>.</summary>
    private async Task<StepRun> RunStepAsync(RunScope scope, Flow flow, Library library, StepNode node,
        RunValues values, long runTicks, CancellationToken flowToken)
    {
        var runId = scope.RunId;
        await Task.Yield(); // run on the pool, never inline in the scheduler loop
        var started = _time.GetTimestamp();
        var offset = _time.GetElapsedTime(runTicks, started);
        List<TimeSpan> retries = [];
        CalledRun? called = null;
        var quiet = false;
        var stopped = false;
        string? summary = null;
        string? skipped = null;
        StepRun Done(StepResult result, int attempts, string? error) =>
            new(result, attempts, error, _time.GetElapsedTime(started), offset, retries, called, quiet, summary, skipped, stopped);
        string? unpicked = null;
        // Values go in now (D-117, D-129): the lock, the provider and a called flow see plain text and Library keys.
        node = DryRun ? Describe(flow, node, values, out unpicked) : StepValues.Fill(flow, node, values, library, out unpicked, _catalog.FirstOrDefault(d => d.Id == node.Step));
        if (unpicked is not null) // a device setting never falls back (D-117, D-130): the step fails
            return new(StepResult.Failed, 1, NotGiven(flow, unpicked), TimeSpan.Zero, offset, retries);
        var descriptor = _catalog.FirstOrDefault(d => d.Id == node.Step);
        var waits = Flows is not null && node.Step == ProviderCatalog.RunFlowId && !IsOff(node, "wait");
        IStepProvider? provider = null;
        if (descriptor is null || (!waits && !_providers.TryGetValue(node.Step, out provider)))
            return new(StepResult.Failed, 1, $"This version can't run \"{node.Step}\" steps.", TimeSpan.Zero, offset, retries);

        var timeout = node.Timeout ?? descriptor.DefaultTimeout;
        var attempts = 1 + Math.Max(0, node.RetryCount ?? 0);
        string? error = null;
        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            try
            {
                if (attempt > 1)
                    await Task.Delay(node.RetryDelay ?? StepSettings.DefaultRetryDelay, _time, flowToken).ConfigureAwait(false);
                flowToken.ThrowIfCancellationRequested(); // a cancelled run starts no further side effects
            }
            catch (OperationCanceledException)
            {
                return Done(StepResult.Cancelled, attempt - 1, error);
            }
            catch (ArgumentOutOfRangeException e) // a delay the timer can't hold
            {
                return Done(StepResult.Failed, attempt - 1, e.Message);
            }
            if (attempt > 1)
                retries.Add(_time.GetElapsedTime(runTicks));
            values.Clear(node.Id); // an attempt starts: the last attempt's values stand (D-129)
            Raise(new NodeStarted(runId, flow.Id, _time.GetUtcNow(), node.Id, attempt)
                { ParentRunId = scope.ParentRunId, ParentNodeId = scope.ParentNodeId });
            if (waits)
            {
                var call = await CallAsync(scope, flow, node, ConfigValidator.CalledFlow(node) ?? "", FlowInputs.Given(node), timeout, flowToken,
                    values, library).ConfigureAwait(false);
                (error, called, quiet) = (call.Error, call.Called ?? called, call.Quiet);
                if (quiet)
                    return Done(StepResult.Cancelled, attempt, null);
            }
            else
            {
                var context = new StepContext(flow, node, descriptor, library, attempt)
                {
                    Chain = scope.Chain,
                    Trigger = scope.Trigger,
                    WantsValue = name => values.Wants(node.Id, name),
                    Variables = node.Step == ProviderCatalog.PowerShellId ? StepValues.Variables(flow, node.Id, values, library, _catalog, CalledFlows) : null,
                    RunNested = Flows is null ? null : (target, token) => NestedAsync(scope, flow, node, target, c => called = c, token),
                };
                var attemptResult = await AttemptAsync(provider!, context, timeout, flowToken).ConfigureAwait(false);
                // Sensitive values never reach the log or history (D-014): an error or summary that holds one shows its title.
                var secrets = DryRun ? [] : StepValues.Secrets(flow, node.Id, values, library, _catalog, CalledFlows);
                error = StepValues.Mask(attemptResult.Error, secrets);
                summary = StepValues.Mask(context.Summary, secrets);
                skipped = attemptResult.SkippedBecause;
                if (skipped is null) // an unmet "Only if" gives nothing (DESIGN §4.5), though its probe may have read values
                    GiveValues(values, node.Id, descriptor, context, error is null);
                called = context.Called ?? called;
                if (attemptResult.EndsFlow)
                {
                    (quiet, stopped) = (true, attemptResult.Stop);
                    return Done(StepResult.Cancelled, attempt, error);
                }
            }
            if (error is null)
                return Done(StepResult.Succeeded, attempt, null);
            values.SetError(node.Id, error);
            if (flowToken.IsCancellationRequested)
                return Done(StepResult.Cancelled, attempt, error);
        }
        return Done(StepResult.Failed, attempts, error);
    }

    /// <summary>A dry run (D-042) fills text with each value's title ("Connected to ‹Device›"), never a real or sample device; the Pick's key still goes into device settings.</summary>
    public bool DryRun { get; init; }

    private ParamValue DescribeParam(Flow flow, ParamValue setting, ParamSpec? spec) => setting switch
    {
        ScalarParam { Text: var text } when spec is not null && StepValues.Takes(spec) => new ScalarParam(StepValues.Describe(flow, text, _catalog)),
        ListParam list when spec is not null && StepValues.TakesItems(spec) => new ListParam(new ValueList<ParamValue>(list.Items.Select(i =>
            i is ScalarParam { Text: var item } ? new ScalarParam(StepValues.Describe(flow, item, _catalog)) : i))),
        _ => setting,
    };

    private StepNode Describe(Flow flow, StepNode step, RunValues values, out string? missing)
    {
        var descriptor = _catalog.FirstOrDefault(d => d.Id == step.Step);
        var shown = descriptor is null ? step : step with
        {
            With = new(step.With.Select(e => KeyValuePair.Create(e.Key, DescribeParam(flow, e.Value, descriptor.FindParam(e.Key))))),
        };
        // A Pick's dry result is its default (or it fails), so its missing key fails as in a real run. Other steps don't run
        // in a dry run, so their Device values (Previous default) aren't known: the reference stays, and the dry step says so.
        var resolved = PickResults.Resolve(shown, (id, value) => values.Device(id, value)
            ?? (flow.FindNode(id) is StepNode p && PickResults.IsPick(p) && value == StepValues.DeviceName ? null : PickResults.Ref(id, value)),
            out missing, descriptor);
        // A dry flow.run passing such a value: the called flow can't check a key nobody knows yet, so it dry-runs with that
        // input's default instead (the validator already checked the reference).
        if (missing is null && resolved.Step == ProviderCatalog.RunFlowId
            && resolved.With.TryGetValue(FlowInputs.Param, out var given) && given is MapParam map
            && map.Entries.Values.Any(v => PickResults.StepIn(v) is not null))
        {
            var kept = new MapParam(new(map.Entries.Where(e => PickResults.StepIn(e.Value) is null)));
            resolved = resolved with
            {
                With = new(resolved.With.Select(e => KeyValuePair.Create(e.Key, e.Key == FlowInputs.Param ? kept : e.Value))),
            };
        }
        return resolved;
    }

    /// <summary>
    /// Why a device setting got no key: "Nothing was picked in "Pick device"." for a Pick's result (D-117), else
    /// ""Set default device" gave no Previous default." (D-130). <paramref name="written"/> is the reference after <c>$result:</c>.
    /// </summary>
    private string NotGiven(Flow flow, string written)
    {
        var (id, value) = PickResults.RefIn(PickResults.Prefix + written) ?? (written, StepValues.DeviceName);
        if (flow.FindNode(id) is not StepNode producer)
            return $"Nothing was picked in \"{id}\".";
        if (PickResults.IsPick(producer) && value == StepValues.DeviceName)
            return $"Nothing was picked in \"{PickResults.NameOf(producer)}\".";
        var descriptor = _catalog.FirstOrDefault(d => d.Id == producer.Step);
        var title = descriptor is null ? value : StepValues.Find(descriptor, value)?.Title ?? value;
        return $"\"{StepValues.NameOf(producer, descriptor)}\" gave no {title}.";
    }

    /// <summary>
    /// Copies what the attempt gave into the run (D-129): all of it on success, only <see cref="ValueSpec.OnFailure"/> values
    /// when it failed. Undeclared names are dropped; a Device value without a kind takes the step type's or the Pick's.
    /// </summary>
    private static void GiveValues(RunValues values, string step, ProviderDescriptor descriptor, StepContext context, bool succeeded)
    {
        foreach (var (name, given) in context.Values)
        {
            if (descriptor.Values.FirstOrDefault(v => v.Name == name) is not { } spec || !succeeded && !spec.OnFailure)
                continue;
            var kind = given.Library ?? spec.Library ?? (PickResults.IsPick(context.Node) ? PickResults.KindOf(context.Node) : null);
            if (given.Kind == ValueKind.Text)
                values.SetText(step, name, given.Text); // trimmed and cut
            else if (given.Kind == ValueKind.Device)
                values.SetDevice(step, name, given.Text, kind);
            else
                values.Set(step, name, given);
        }
    }

    private static bool IsOff(StepNode node, string param) =>
        node.With.TryGetValue(param, out var value) && value is ScalarParam { Text: var text }
        && bool.TryParse(text, out var on) && !on;

    /// <summary>
    /// One try of a <c>flow.run</c> that waits (DESIGN §2.2): runs the called flow nested and maps its outcome. The
    /// nested run honours the token itself, so it is awaited to the end (its steps are bounded by their own timeouts).
    /// </summary>
    /// <param name="values">The caller's values: text inputs are filled from them, and a <c>flow.run</c> gives the called
    /// flow's outputs into them (T23.7). Null for a menu's call.</param>
    /// <param name="callerLibrary">The library the caller was validated with, for device names in filled text.</param>
    private async Task<CallTry> CallAsync(RunScope scope, Flow caller, StepNode node, string target,
        IReadOnlyDictionary<string, string> inputs, TimeSpan timeout, CancellationToken flowToken,
        RunValues? values = null, Library? callerLibrary = null)
    {
        var resolved = Flows!.Resolve(target);
        if (FlowCalls.Guard(scope.Chain, target, resolved.Name) is { } loop)
            return new(loop, null, false);
        if (resolved.Flow is not { } flow)
            return new(resolved.Refusal, null, false);
        // The caller's own $names are already resolved (RunCoreAsync).
        if (FlowInputs.Check(flow, resolved.Library, inputs, caller.Name) is { } missing)
            return new(missing, null, false);
        HashSet<string>? sensitive = null;
        if (values is not null && callerLibrary is not null && inputs.Keys.Any(n => flow.Inputs.TryGetValue(n, out var i) && i.IsText))
        {
            // Text inputs take values from the caller's steps (T23.7): filled here, so the called flow sees plain text.
            var secrets = StepValues.Secrets(caller, node.Id, values, callerLibrary, _catalog, CalledFlows);
            var filled = new Dictionary<string, string>(inputs, StringComparer.Ordinal);
            foreach (var (name, text) in inputs)
            {
                if (!flow.Inputs.TryGetValue(name, out var input) || !input.IsText)
                    continue;
                filled[name] = StepValues.FillText(caller, text, values, callerLibrary, singleLine: false);
                if (secrets.Any(s => filled[name].Contains(s.Text, StringComparison.Ordinal)))
                    (sensitive ??= new(StringComparer.Ordinal)).Add(name);
            }
            inputs = filled;
        }
        CancellationTokenSource timeoutCancel;
        try
        {
            timeoutCancel = new CancellationTokenSource(timeout, _time);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new($"A timeout of {Duration.Format(timeout)} is longer than this version supports.", null, false);
        }
        using var ownedTimeout = timeoutCancel;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(flowToken, timeoutCancel.Token);
        RunResult result;
        try
        {
            result = await RunCoreAsync(flow, resolved.Library, inputs, [.. scope.Chain, flow.Id], scope.RunId, node.Id, linked.Token, scope.Trigger,
                sensitive).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // a runner bug in the nested run fails this step only
        catch (Exception e)
#pragma warning restore CA1031
        {
            return new(e.Message, null, false);
        }
        var called = new CalledRun(result.RunId, flow.Id, flow.Name, result.Outcome, [.. result.Nodes]);
        if (values is not null && node.Step == ProviderCatalog.RunFlowId)
        {
            // What the called flow gives back (T23.7); an output it no longer has stays missing: the fallback, else empty.
            foreach (var (name, text) in result.Outputs)
            {
                values.SetText(node.Id, name, text);
                if (result.SensitiveOutputs.Contains(name))
                    values.MarkSensitive(node.Id, name); // stays hidden in the caller's errors and summaries (D-014)
            }
        }
        return result.Outcome switch
        {
            FlowOutcome.Succeeded or FlowOutcome.PartiallySucceeded => new(null, called, false),
            FlowOutcome.Failed => new($"{flow.Name} didn't finish: {FirstFailure(flow, result.Nodes)}", called, false),
            _ when result.Stopped => new(null, called, false), // the called flow ended itself on purpose (T24.1): this step is done
            _ when flowToken.IsCancellationRequested => new("Cancelled.", called, false),
            _ when timeoutCancel.IsCancellationRequested => new($"{flow.Name} didn't finish in {Duration.Format(timeout)}.", called, false),
            _ => new(null, called, true), // ended quietly from inside (a menu with nothing picked, D-054)
        };
    }

    /// <summary>
    /// A step that runs a flow nested (a menu with <c>wait</c>): same rules as a waiting <c>flow.run</c>, but the
    /// step's own token already carries its timeout, so the call adds none.
    /// </summary>
    private async Task<NestedAnswer> NestedAsync(RunScope scope, Flow caller, StepNode node, string target, Action<CalledRun> record,
        CancellationToken token)
    {
        var call = await CallAsync(scope, caller, node, target, NoInputs, Timeout.InfiniteTimeSpan, token).ConfigureAwait(false);
        if (call.Called is { } called)
            record(called);
        return new(call.Error, call.Quiet);
    }

    /// <summary>The called flow's first failure in its own words (#56).</summary>
    private static string FirstFailure(Flow flow, IEnumerable<NodeResult> nodes)
    {
        var failure = nodes.Where(n => n.Outcome == NodeOutcome.Failed && flow.FindNode(n.NodeId) is StepNode)
            .OrderBy(n => n.Order == 0 ? int.MaxValue : n.Order).FirstOrDefault();
        if (failure is null)
            return "a step didn't work.";
        if (!string.IsNullOrWhiteSpace(failure.Error))
            return failure.Error.Trim();
        var step = (StepNode)flow.FindNode(failure.NodeId)!;
        var name = step.Name is { Length: > 0 } own ? own : ProviderCatalog.Find(step.Step)?.Title ?? step.Id;
        return $"{name} didn't work.";
    }

    /// <returns>No error on success, else why it failed.</returns>
    private async Task<AttemptResult> AttemptAsync(IStepProvider provider, StepContext context, TimeSpan timeout, CancellationToken flowToken)
    {
        CancellationTokenSource timeoutCancel;
        try
        {
            timeoutCancel = new CancellationTokenSource(timeout, _time);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new($"A timeout of {Duration.Format(timeout)} is longer than this version supports.");
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
            return new(flowToken.IsCancellationRequested ? "Cancelled." : $"Waited {Duration.Format(timeout)} for another step using the same device.");
        }
        using var release = held;
        if (linked.IsCancellationRequested) // AcquireAsync with no resources doesn't check
            return new(flowToken.IsCancellationRequested ? "Cancelled." : $"Didn't finish within {Duration.Format(timeout)}.");
        if (context.Attempt == 1 && await UnmetAsync(context, linked.Token).ConfigureAwait(false) is { } unmet)
            return new(null, SkippedBecause: unmet); // Succeeded with a note, never NodeOutcome.Skipped (D-081)
        if (linked.IsCancellationRequested) // the probe used up the time or the run was cancelled meanwhile
            return new(flowToken.IsCancellationRequested ? "Cancelled." : $"Didn't finish within {Duration.Format(timeout)}.");
        // On the pool, so a provider that blocks before returning its task is still bounded by WaitAsync.
        var execution = Task.Run(() => provider.ExecuteAsync(context, linked.Token), CancellationToken.None);
        try
        {
            // WaitAsync ends the attempt on time even if the provider ignores its token.
            await execution.WaitAsync(linked.Token).ConfigureAwait(false);
            return new(null);
        }
        catch (OperationCanceledException) when (linked.IsCancellationRequested)
        {
            Observe(execution);
            return new(flowToken.IsCancellationRequested ? "Cancelled." : $"Didn't finish within {Duration.Format(timeout)}.");
        }
        catch (StepEndsFlowException e)
        {
            return new(e.Message, EndsFlow: true, Stop: e.IsStop);
        }
#pragma warning disable CA1031
        catch (Exception e)
#pragma warning restore CA1031
        {
            return new(e.Message);
        }
    }

    /// <summary>
    /// "Only if" (D-081), read once at the first attempt, after the lock, inside the timeout. Returns the probe's sentence
    /// when the condition is not met (skip the step); null = run it. Anything that stops the answer (no probe, a throw,
    /// a timeout) means "cannot tell": run, never skip on doubt.
    /// </summary>
    private async Task<string?> UnmetAsync(StepContext context, CancellationToken token)
    {
        if (context.Node.Only is not { } only || only == StepNode.OnlyAlways
            || context.Descriptor.Conditions.FirstOrDefault(c => c.Id == only) is not { } condition
            || !_probes.TryGetValue(condition.Fact, out var probe))
            return null;
        try
        {
            var reading = await probe.ReadAsync(context, token).WaitAsync(token).ConfigureAwait(false);
            return reading.Value == condition.Expected ? null : reading.Text;
        }
#pragma warning disable CA1031 // fail-soft: a probe that can't answer lets the step run (D-081)
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }

    /// <summary>
    /// The descriptor's lock names with "{param}" replaced by that param's value ("audio"). A Bluetooth or Android
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
            when library.ResolveBluetooth(text) is { } device => device.Address.ToUpperInvariant(),
        { Kind: ParamKind.LibraryRef, Library: LibraryKind.Android }
            when library.Android.FirstOrDefault(d => d.Key == text) is { } tv => tv.Serial.ToUpperInvariant(),
        _ => text,
    };

    // A provider abandoned on timeout may still fault later; observe it so it never surfaces as unobserved.
    private static void Observe(Task task) =>
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
