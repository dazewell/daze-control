using System.Collections.Concurrent;
using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;
using DazeControl.Ipc;
using DazeControl.Runtime.Lifecycle;
using DazeControl.Runtime.Providers;
using DazeControl.Runtime.Triggers;

namespace DazeControl.Runtime.Control;

/// <summary>What only the message thread knows, published for the pipe (replaced whole, never mutated).</summary>
/// <param name="ShortcutProblems">"Alt+Z: Another app uses Alt+Z. Try another one."</param>
public sealed record ShellStatus(bool ShortcutsPaused, IReadOnlyList<string> ShortcutProblems)
{
    public static ShellStatus Empty { get; } = new(false, []);
}

/// <summary>Where the engine's files are and how it was started, for <c>status</c> and <c>autostart</c>.</summary>
/// <param name="ConfigArgument">The config path the engine was started with, null for the default one; autostart
/// passes it on so the next sign-in loads the same flows.</param>
public sealed record EngineInfo(string Version, int ProcessId, string ExePath, string ConfigPath, string? ConfigArgument = null);

/// <summary>A desktop shortcut's run that couldn't start, for the pop-up (UX §5.7 #24).</summary>
/// <param name="Title">The flow's name, or "Desktop shortcut" when the flow is gone.</param>
public sealed record RunRefusal(string FlowId, string Title, string Message);

/// <summary>
/// Every trigger and pipe request goes through here (FEATURES F2, F8; ARCHITECTURE → IPC): finds the flow by id or
/// name in the current config, starts it through the <see cref="FlowLauncher"/>, and answers the pipe ops. Thread-safe;
/// the shell's state arrives as <see cref="Shell"/> snapshots.
/// </summary>
public sealed class EngineController(Func<ConfigSnapshot> config, FlowLauncher launcher, RunHistory? history,
    FlowRunner dryRunner, Autostart autostart, EngineInfo info, Action<string> log, IDeviceQueries? devices = null,
    FlowRunner? stepRunner = null, DesktopShortcuts? shortcuts = null, IGamepadControl? gamepads = null)
{
    /// <summary>A step test runs as a flow with this id prefix; <c>cancel</c> with that id stops it.</summary>
    public const string TestPrefix = "test:";

    /// <summary><c>devices gamepads</c>: the pads on now.</summary>
    public const string GamepadKind = "gamepads";

    /// <summary>The trigger the history shows for a run from a desktop shortcut (D-049).</summary>
    public const string DesktopTrigger = "Desktop shortcut";

    /// <summary>UX §5.7 #24.</summary>
    public const string ShortcutFlowGone = "This shortcut's flow isn't there anymore. You can delete the shortcut.";

    private readonly ConcurrentDictionary<string, CancellationTokenSource> _tests = new(StringComparer.Ordinal);
    private volatile ShellStatus _shell = ShellStatus.Empty;

    // record-hotkey: the connections whose shortcut box is recording, each with its "connection gone" hook.
    private readonly Dictionary<CancellationToken, CancellationTokenRegistration> _recorders = [];

    public ShellStatus Shell
    {
        get => _shell;
        set => _shell = value;
    }

    /// <summary>Longest flag name <c>dctl flag</c> takes (a name is a few words).</summary>
    public const int MaxFlagName = 200;

    /// <summary>
    /// Reads (new value null) or sets a flag by name and returns what it is now (<c>dctl flag</c>, T27.8): the engine's
    /// <c>FlagStore</c>, set by the host. Null = no flag store, the op says so.
    /// </summary>
    public Func<string, bool?, bool>? FlagAccess { get; set; }

    /// <summary>
    /// Pauses (true), resumes (false) or reads (null) the shortcuts, like the tray item, and returns whether they are paused now
    /// (<c>dctl pause</c>, T27.8). The host runs it on the message thread, where the shortcuts are registered.
    /// </summary>
    public Func<bool?, Task<bool>>? PauseAccess { get; set; }

    /// <summary>
    /// An <see cref="PipeRequest.ExactId"/> run (a desktop shortcut) that didn't start: nobody sees the pipe's answer, so
    /// the shell says it in the pop-up. Any thread.
    /// </summary>
    public event Action<RunRefusal>? RunRefused;

    /// <summary>
    /// True when the first connection starts recording a shortcut, false when the last one stops or goes away (D-072).
    /// Raised in order, under a lock, from a pipe thread: the handler only posts.
    /// </summary>
    public event Action<bool>? HotkeyRecordingChanged;

    /// <summary>
    /// A runner whose steps do nothing, for <c>dctl run --dry-run</c> and the editor's Dry run: walks the flow's
    /// structure only. A step whose device isn't in the library fails, so the dry run shows what would be skipped.
    /// </summary>
    public static FlowRunner CreateDryRunner(TimeProvider time) =>
        new(ProviderCatalog.All.Select(d => new DryProvider(d.Id)), time) { DryRun = true };

    /// <summary>"Only run if" (T25.1, D-150); null = flows start unguarded (tests, an engine without a test-step runner).</summary>
    public FlowGuard? Guard { get; set; }

    /// <summary>Starts a runnable flow by hand (hotkey, tray, pipe); null when it can't run, was held back or the trigger was ignored.</summary>
    public Task<RunResult?> TriggerAsync(string flowId, string trigger) => GuardedAsync(flowId, trigger, byHand: true);

    /// <summary>A schedule's start (M20): "Only run if" always applies.</summary>
    public Task<RunResult?> TriggerScheduledAsync(string flowId, string trigger) => GuardedAsync(flowId, trigger, byHand: false);

    /// <summary>Starts this version of the flow (the one the caller checked), not whatever the config holds now.</summary>
    public Task<RunResult?> TriggerAsync(FlowState state, string trigger) => TriggerAsync(state, state.FlowId, trigger, null);

    /// <summary>
    /// Reads "Only run if" first when it applies, then starts the version the config holds once the checks are done (they take
    /// time; a reload meanwhile wins). Held back = null, with the guard's log line and note.
    /// </summary>
    private async Task<RunResult?> GuardedAsync(string flowId, string trigger, bool byHand)
    {
        var state = config().Flows.FirstOrDefault(f => f.FlowId == flowId);
        if (await HeldBackAsync(state, trigger, byHand, CancellationToken.None).ConfigureAwait(false) is not null)
            return null;
        if (state?.Running is { OnlyIf.Count: > 0 })
            state = config().Flows.FirstOrDefault(f => f.FlowId == flowId);
        return await TriggerAsync(state, flowId, trigger).ConfigureAwait(false);
    }

    /// <summary>The guard's answer for this start, null = start (no guard, nothing to read, or the checks hold).</summary>
    private async Task<string?> HeldBackAsync(FlowState? state, string trigger, bool byHand, CancellationToken cancellationToken)
    {
        if (Guard is not { } guard || state?.Running is not { Enabled: true } flow || !FlowGuard.Applies(flow, byHand))
            return null;
        return await guard.HeldBackAsync(StepDefaults.Apply(flow, state.Defaults), state.Library, trigger, cancellationToken).ConfigureAwait(false);
    }

    private Task<RunResult?> TriggerAsync(FlowState? state, string flowId, string trigger, IReadOnlyDictionary<string, string>? inputs = null)
    {
        if (state?.Running is not { Enabled: true } flow)
        {
            log($"run: {flowId} ({trigger}) not runnable");
            return Task.FromResult<RunResult?>(null);
        }
        log($"run: {flowId} triggered by {trigger}");
        var ready = StepDefaults.Apply(flow, state.Defaults);
        // Device inputs resolve here (an answered ask is never asked again, D-107); text inputs travel beside the flow.
        var run = inputs is null
            ? launcher.TriggerAsync(ready, state.Library, trigger)
            : launcher.TriggerAsync(FlowInputs.Apply(ready, inputs, keepAsked: true), state.Library, trigger, null, inputs);
        _ = run.ContinueWith(t =>
        {
            if (t.IsFaulted)
                log($"run: {flowId} failed in the runner: {t.Exception!.GetBaseException()}");
            else if (t.Result is null)
                log($"run: {flowId} ({trigger}) ignored, already running");
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return run;
    }

    public async Task<PipeMessage> HandleAsync(PipeRequest request, CancellationToken cancellationToken)
    {
        var id = request.Id;
        switch (request.Op)
        {
            case Ops.Run:
                return await RunAsync(request, cancellationToken).ConfigureAwait(false);
            case Ops.Cancel:
                return Cancel(request);
            case Ops.List:
                return new PipeMessage { Id = id, Ok = true, Flows = [.. config().Flows.Select(Describe)] };
            case Ops.Status:
                return new PipeMessage { Id = id, Ok = true, Status = Status() };
            case Ops.Validate:
                return new PipeMessage { Id = id, Ok = true, Problems = [.. config().Problems.Select(Describe)] };
            case Ops.History:
                return History(request);
            case Ops.Autostart:
                return Autostart(request);
            case Ops.Flag:
                return Flag(request);
            case Ops.Pause:
                return await PauseAsync(request).ConfigureAwait(false);
            case Ops.Devices:
                return await DevicesAsync(request, cancellationToken).ConfigureAwait(false);
            case Ops.AndroidApps:
                return await AndroidAppsAsync(request, cancellationToken).ConfigureAwait(false);
            case Ops.CaptureDisplayLayout:
                return await CaptureDisplayLayoutAsync(request, cancellationToken).ConfigureAwait(false);
            case Ops.TestStep:
                return await TestStepAsync(request, cancellationToken).ConfigureAwait(false);
            case Ops.RecordHotkey:
                return RecordHotkey(request, cancellationToken);
            case Ops.Shortcuts:
            case Ops.CreateShortcut:
            case Ops.RemoveShortcut:
                // COM and file I/O off the pipe's reader: the request runs on a pool thread.
                return await Task.Run(() => Shortcuts(request), cancellationToken).ConfigureAwait(false);
            default:
                return PipeMessage.Fail(id, $"Unknown request \"{request.Op}\".");
        }
    }

    /// <param name="connection">The request's connection: when it ends (the editor quit or crashed), so does its recording.</param>
    private PipeMessage RecordHotkey(PipeRequest request, CancellationToken connection)
    {
        if (request.On is not { } on)
            return PipeMessage.Fail(request.Id, "record-hotkey needs on: true or false.");
        if (on)
        {
            lock (_recorders)
            {
                if (!_recorders.TryAdd(connection, default))
                    return new PipeMessage { Id = request.Id, Ok = true }; // focus came again
                if (_recorders.Count == 1)
                    HotkeyRecordingChanged?.Invoke(true);
            }
            // Outside the lock: a connection already gone runs the hook right here, and the hook takes the lock.
            var hook = connection.Register(() => StopRecording(connection));
            var stale = false;
            lock (_recorders)
            {
                if (_recorders.ContainsKey(connection))
                    _recorders[connection] = hook;
                else
                    stale = true; // stopped meanwhile
            }
            if (stale)
                hook.Dispose();
        }
        else
        {
            StopRecording(connection);
        }
        return new PipeMessage { Id = request.Id, Ok = true };
    }

    private void StopRecording(CancellationToken connection)
    {
        CancellationTokenRegistration hook;
        lock (_recorders)
        {
            if (!_recorders.Remove(connection, out hook))
                return;
            if (_recorders.Count == 0)
                HotkeyRecordingChanged?.Invoke(false);
        }
        hook.Dispose();
    }

    private async Task<PipeMessage> DevicesAsync(PipeRequest request, CancellationToken cancellationToken)
    {
        if (gamepads is not null && request.Kind == GamepadKind)
        {
            return new PipeMessage
            {
                Id = request.Id,
                Ok = true,
                Devices = [.. gamepads.List().Select(p => new DeviceInfo
                {
                    Name = $"Controller {p.Slot + 1}",
                    Detail = p.Battery,
                    State = "connected",
                })],
            };
        }
        if (devices is null)
            return PipeMessage.Fail(request.Id, "This engine can't list devices.");
        if (request.Kind is not { } kind || !devices.Kinds.Contains(kind, StringComparer.Ordinal))
            return PipeMessage.Fail(request.Id, $"Say which devices: {string.Join(", ", devices.Kinds)}.");
        try
        {
            var list = await devices.ListAsync(kind, cancellationToken).ConfigureAwait(false);
            return new PipeMessage { Id = request.Id, Ok = true, Devices = [.. list] };
        }
        catch (StepFailedException e)
        {
            return PipeMessage.Fail(request.Id, e.Message);
        }
    }

    /// <summary>The apps of an Android device for the editor's picker (D-093): from the saved list unless asked to refresh.</summary>
    private async Task<PipeMessage> AndroidAppsAsync(PipeRequest request, CancellationToken cancellationToken)
    {
        if (devices is null)
            return PipeMessage.Fail(request.Id, "This engine can't list devices.");
        if (string.IsNullOrWhiteSpace(request.Address))
            return PipeMessage.Fail(request.Id, "Say which Android device (its address).");
        try
        {
            var list = await devices.AndroidAppsAsync(request.Address.Trim(), request.Adb, request.Refresh, cancellationToken).ConfigureAwait(false);
            return new PipeMessage { Id = request.Id, Ok = true, Devices = [.. list] };
        }
        catch (StepFailedException e)
        {
            return PipeMessage.Fail(request.Id, e.Message);
        }
    }

    /// <summary>
    /// "Test this step" (UX §5.4): runs one step of the config text the editor sent, alone (no edges, stop on
    /// failure), with the real providers and the engine's locks, so it waits for a running flow using the same
    /// device. No history, no pop-up. One test per flow at a time.
    /// </summary>
    private async Task<PipeMessage> TestStepAsync(PipeRequest request, CancellationToken cancellationToken)
    {
        var id = request.Id;
        if (stepRunner is null)
            return PipeMessage.Fail(id, "This engine can't test steps.");
        if (request.Config is null || string.IsNullOrEmpty(request.Flow) || string.IsNullOrEmpty(request.Node))
            return PipeMessage.Fail(id, "Say which step to test.");
        var load = ConfigYaml.Load(request.Config);
        if (load.Config?.Flows.FirstOrDefault(f => f.Id == request.Flow) is not { } flow
            || flow.FindNode(request.Node) is not StepNode step)
            return PipeMessage.Fail(id, "That step isn't in the flow.");

        var testId = TestPrefix + flow.Id;
        // Inputs come along, so a step using $name validates and runs with the defaults (D-064).
        var test = new Flow(testId, flow.Name, [step with { After = [], OnFailure = FailurePolicy.Stop }]) { Inputs = flow.Inputs };
        // The on-screen defaults apply to a test like to a run (D-080).
        var filled = StepDefaults.Apply(test, load.Config.Settings.StepDefaults);
        // Mapping replaces a bad value (timeout: nonsense) with the default: that must refuse the test, not run it.
        // Edges and the policy are dropped for the test, so their problems don't count.
        var mapped = $"flows.{flow.Id}.nodes.{step.Id}";
        var prefix = $"flows.{testId}.nodes.{step.Id}";
        var problem = load.Problems.Where(p => p.Path == mapped || p.Path.StartsWith(mapped + ".", StringComparison.Ordinal))
            .Where(p => !p.Path.StartsWith(mapped + ".after", StringComparison.Ordinal) && p.Path != mapped + ".onFailure")
            .Concat(ConfigValidator.Validate(new Configuration { Library = load.Config.Library, Flows = [test] })
                .Where(p => p.Path.StartsWith(prefix, StringComparison.Ordinal)))
            .FirstOrDefault(p => p.Severity == Severity.Error);
        if (problem is not null)
            return PipeMessage.Fail(id, $"Fix this step first: {problem.Message}");

        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (!_tests.TryAdd(testId, cancel))
            return PipeMessage.Fail(id, "This flow is already testing a step.");
        try
        {
            log($"test: {flow.Id}/{step.Id} ({step.Step})");
            var result = await stepRunner.RunAsync(filled, load.Config.Library, cancel.Token).ConfigureAwait(false);
            return new PipeMessage { Id = id, Ok = true, Run = Record(result, testId, flow.Name, "Test") };
        }
        finally
        {
            _tests.TryRemove(testId, out _);
        }
    }

    private async Task<PipeMessage> CaptureDisplayLayoutAsync(PipeRequest request, CancellationToken cancellationToken)
    {
        if (devices is null)
            return PipeMessage.Fail(request.Id, "This engine can't read the screens.");
        if (string.IsNullOrWhiteSpace(request.Key))
            return PipeMessage.Fail(request.Id, "Say what to call the layout.");
        try
        {
            var layout = await devices.CaptureDisplayLayoutAsync(request.Key, cancellationToken).ConfigureAwait(false);
            log($"displays: captured layout {layout.Key} ({layout.Monitors.Count} screen(s))");
            return new PipeMessage { Id = request.Id, Ok = true, Yaml = ConfigYaml.Snippet(layout) };
        }
        catch (StepFailedException e)
        {
            return PipeMessage.Fail(request.Id, e.Message);
        }
    }

    private async Task<PipeMessage> RunAsync(PipeRequest request, CancellationToken cancellationToken)
    {
        var id = request.Id;
        if (request.DryRun && request.Config is not null)
            return await DryRunSentAsync(request, request.Trigger ?? "Editor", cancellationToken).ConfigureAwait(false);
        if (request.ConfigPath is { } wanted && !SameFile(wanted, info.ConfigPath))
            return Refuse(request, request.Flow ?? "", DesktopTrigger,
                $"This shortcut is for the flows in {wanted}, but Daze Control is running {info.ConfigPath}.");
        var state = request.ExactId ? FindById(request.Flow) : Find(request.Flow);
        if (state is null)
            return Refuse(request, request.Flow ?? "", DesktopTrigger, ShortcutFlowGone, NotFound(id, request.Flow));
        if (state.Running is not { } flow)
            return Refuse(request, state.FlowId, NameOf(state), $"{NameOf(state)} needs fixing before it can run.");
        if (!flow.Enabled)
            return Refuse(request, state.FlowId, NameOf(state), $"{NameOf(state)} is turned off.");
        var trigger = request.Trigger ?? "Command line";
        // Inputs given from outside (T24.9): checked before anything starts, like a call's (the values are data, never logged).
        var inputs = request.Inputs is { Count: > 0 } given ? new Dictionary<string, string>(given, StringComparer.Ordinal) : null;
        if (inputs is not null && FlowInputs.CheckGiven(flow, state.Library, inputs) is { } bad)
            return Refuse(request, state.FlowId, NameOf(state), bad, PipeMessage.BadUsage(id, bad));

        if (request.DryRun)
        {
            var dry = await dryRunner.RunAsync(WithoutRetries(StepDefaults.Apply(flow, state.Defaults)), state.Library, [], cancellationToken, "", inputs).ConfigureAwait(false);
            return new PipeMessage { Id = id, Ok = true, Run = Record(dry, flow.Id, flow.Name, trigger + " (dry run)") };
        }

        // "Only run if" with "also when I start it" (T25.1): read before the start, so dctl can say why nothing ran.
        if (await HeldBackAsync(state, trigger, byHand: true, cancellationToken).ConfigureAwait(false) is { } held)
            return PipeMessage.Fail(id, held);
        var run = TriggerAsync(state, state.FlowId, trigger, inputs);
        if (!request.Wait)
        {
            // Ignored triggers resolve at once; anything else has started.
            if (run.IsCompleted && run.Result is null)
                return PipeMessage.Fail(id, $"{flow.Name} is already running.");
            return new PipeMessage { Id = id, Ok = true };
        }
        var result = await run.WaitAsync(cancellationToken).ConfigureAwait(false);
        return result is null
            ? PipeMessage.Fail(id, $"{flow.Name} is already running.")
            // A queued run may be a later trigger's (its version and trigger): answer with what history recorded.
            : new PipeMessage
            {
                Id = id, Ok = true,
                Run = history?.Recent.FirstOrDefault(r => r.RunId == result.RunId) ?? Record(result, flow.Id, flow.Name, trigger),
            };
    }

    /// <summary>
    /// The editor's Dry run (UX §5.5): walks the flow as the editor shows it (<see cref="PipeRequest.Config"/>), not
    /// the saved one, so a step whose device is missing shows as failed. Other problems (a loop, a bad timeout)
    /// refuse it: the steps would not run in that shape.
    /// </summary>
    private async Task<PipeMessage> DryRunSentAsync(PipeRequest request, string trigger, CancellationToken cancellationToken)
    {
        var load = ConfigYaml.Load(request.Config!);
        if (load.Config?.Flows.FirstOrDefault(f => f.Id == request.Flow) is not { } flow)
            return NotFound(request.Id, request.Flow);
        var path = $"flows.{flow.Id}";
        var nodes = path + ".nodes.";
        // Ids may contain dots: a step's values are "<nodes><id>.with" for one of the flow's own ids, nothing else.
        var values = flow.Nodes.Select(n => nodes + n.Id + ".with").ToList();
        bool IsStepValue(string p) => values.Exists(v => p == v || p.StartsWith(v + ".", StringComparison.Ordinal));
        var blocking = load.Problems.Concat(ConfigValidator.Validate(load.Config))
            .Where(p => p.Severity == Severity.Error && (p.Path == path || p.Path.StartsWith(path + ".", StringComparison.Ordinal)))
            .FirstOrDefault(p => !IsStepValue(p.Path));
        if (blocking is not null)
            return PipeMessage.Fail(request.Id, $"Fix this first: {blocking.Message}");
        var dry = await dryRunner.RunAsync(WithoutRetries(StepDefaults.Apply(flow, load.Config.Settings.StepDefaults)), load.Config.Library, cancellationToken).ConfigureAwait(false);
        return new PipeMessage { Id = request.Id, Ok = true, Run = Record(dry, flow.Id, flow.Name, trigger + " (dry run)") };
    }

    /// <summary>The pipe's answer; for a desktop shortcut also the pop-up's words (D-049).</summary>
    private PipeMessage Refuse(PipeRequest request, string flowId, string title, string message, PipeMessage? answer = null)
    {
        if (request.ExactId && !request.DryRun)
        {
            log($"run: {flowId} ({request.Trigger ?? DesktopTrigger}) refused: {message}");
            RunRefused?.Invoke(new RunRefusal(flowId, title, message));
        }
        return answer ?? PipeMessage.Fail(request.Id, message);
    }

    /// <summary>
    /// <c>shortcuts</c> lists ours; <c>create-shortcut</c> and <c>remove-shortcut</c> take a flow id or name, like
    /// <c>dctl</c>. A removed flow's shortcut is removed by its id. Windows errors are answers, never crashes (fail-soft).
    /// </summary>
    private PipeMessage Shortcuts(PipeRequest request)
    {
        var id = request.Id;
        if (shortcuts is null)
            return PipeMessage.Fail(id, "This engine can't make desktop shortcuts.");
        // The editor may have another file open than the engine runs: its ids would reach the wrong flows' shortcuts.
        if (request.ConfigPath is { } wanted && !SameFile(wanted, info.ConfigPath))
            return PipeMessage.Fail(id, $"Daze Control is running the flows in {info.ConfigPath}, not this file.");
        try
        {
            switch (request.Op)
            {
                case Ops.Shortcuts:
                    return new PipeMessage { Id = id, Ok = true, Shortcuts = [.. shortcuts.List().Select(Describe)] };
                case Ops.CreateShortcut:
                {
                    var found = request.ExactId ? FindById(request.Flow) : Find(request.Flow);
                    if (found is not { } state || (state.Running ?? state.Written) is not { } flow)
                        return NotFound(id, request.Flow);
                    var made = shortcuts.Create(flow);
                    log($"shortcuts: {flow.Id} → {made.Path}");
                    return new PipeMessage { Id = id, Ok = true, Shortcuts = [Describe(made)] };
                }
                default:
                {
                    if (string.IsNullOrWhiteSpace(request.Flow))
                        return NotFound(id, request.Flow);
                    var listed = shortcuts.List();
                    // The editor names an id: a shortcut already gone is removed, never another flow found by name.
                    var flowId = request.ExactId || listed.Any(s => s.FlowId == request.Flow) ? request.Flow : Find(request.Flow)?.FlowId;
                    if (flowId is null)
                        return NotFound(id, request.Flow);
                    var removed = listed.Where(s => s.FlowId == flowId).Select(Describe).ToArray();
                    shortcuts.Remove(flowId);
                    log($"shortcuts: {flowId} removed ({removed.Length})");
                    return new PipeMessage { Id = id, Ok = true, Shortcuts = removed };
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            log($"shortcuts: {request.Op} {request.Flow}: {e.Message}");
            return PipeMessage.Fail(id, $"Windows didn't let Daze Control change the desktop: {e.Message}");
        }
    }

    private ShortcutInfo Describe(DesktopShortcut shortcut)
    {
        var flow = FindById(shortcut.FlowId) is { } state ? state.Running ?? state.Written : null;
        return new ShortcutInfo
        {
            FlowId = shortcut.FlowId,
            Name = shortcut.Name,
            Path = shortcut.Path,
            FlowExists = flow is not null,
            CurrentName = flow is not null && DesktopShortcuts.HasCurrentName(shortcut, flow),
        };
    }

    /// <summary>A dry step that fails (missing device) must not wait out its retry delays: a preview answers at once.</summary>
    private static Flow WithoutRetries(Flow flow) =>
        flow with { Nodes = [.. flow.Nodes.Select(n => n is StepNode { RetryCount: not null } step ? step with { RetryCount = null, RetryDelay = null } : n)] };

    private static RunRecord Record(RunResult result, string flowId, string name, string trigger) =>
        new(result.RunId, flowId, name, trigger, result.Started, result.Duration, result.Outcome, [.. result.Nodes]);

    /// <summary>A run keeps going when its flow leaves the config, so a raw id that is running can be cancelled.</summary>
    private PipeMessage Cancel(PipeRequest request)
    {
        var id = request.Id;
        if (request.Flow is { } test && _tests.TryGetValue(test, out var testing))
        {
            testing.Cancel();
            return new PipeMessage { Id = id, Ok = true };
        }
        var running = launcher.RunningFlowIds;
        string flowId, name;
        if (request.Flow is { } text && running.Contains(text, StringComparer.Ordinal))
            (flowId, name) = (text, Find(text) is { } byId ? NameOf(byId) : text);
        else if (Find(request.Flow) is { } state)
            (flowId, name) = (state.FlowId, NameOf(state));
        else
            return NotFound(id, request.Flow);
        return launcher.Cancel(flowId) ? new PipeMessage { Id = id, Ok = true } : PipeMessage.Fail(id, $"{name} isn't running.");
    }

    /// <summary>
    /// History outlives the config: the filter matches a recorded flow id, then the config's id or name, then the
    /// newest recorded name.
    /// </summary>
    private PipeMessage History(PipeRequest request)
    {
        IEnumerable<RunRecord> runs = history?.Recent ?? [];
        if (request.Flow is { } text)
        {
            if (!runs.Any(r => r.FlowId == text))
            {
                if (Find(text) is { } state)
                    text = state.FlowId;
                else if (runs.LastOrDefault(r => string.Equals(r.FlowName, text, StringComparison.OrdinalIgnoreCase)) is { } byName)
                    text = byName.FlowId;
                else
                    return NotFound(request.Id, request.Flow);
            }
            runs = runs.Where(r => r.FlowId == text);
        }
        runs = runs.OrderByDescending(r => r.Started);
        if (request.Limit is > 0 and var limit)
            runs = runs.Take(limit);
        return new PipeMessage { Id = request.Id, Ok = true, History = [.. runs] };
    }

    private PipeMessage Flag(PipeRequest request)
    {
        var name = request.Key?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > MaxFlagName || name.Any(char.IsControl))
            return PipeMessage.BadUsage(request.Id, $"flag needs a name (up to {MaxFlagName} characters), then on or off.");
        if (FlagAccess is not { } access)
            return PipeMessage.Fail(request.Id, "Flags aren't available in this engine.");
        var now = access(name, request.Enable);
        if (request.Enable is { } enable)
            log($"flag: \"{name}\" {(enable ? "on" : "off")} (command line)"); // the name is the user's own word, never a value
        return new PipeMessage { Id = request.Id, Ok = true, State = now };
    }

    private async Task<PipeMessage> PauseAsync(PipeRequest request)
    {
        if (PauseAccess is not { } access)
            return PipeMessage.Fail(request.Id, "Pausing shortcuts isn't available in this engine.");
        var paused = await access(request.Enable).ConfigureAwait(false);
        if (request.Enable is not null)
            log($"hotkey: shortcuts {(paused ? "paused" : "resumed")} (command line)");
        return new PipeMessage { Id = request.Id, Ok = true, State = paused };
    }

    private PipeMessage Autostart(PipeRequest request)
    {
        if (request.Enable == true)
            autostart.Enable(info.ExePath, info.ConfigArgument);
        else if (request.Enable == false)
            autostart.Disable();
        if (request.Enable is { } enable)
            log($"autostart: {(enable ? "on" : "off")}");
        var status = autostart.Read();
        var state = status.State switch
        {
            AutostartState.On => "on",
            AutostartState.DisabledByWindows => "disabledByWindows",
            _ => "off",
        };
        return new PipeMessage { Id = request.Id, Ok = true, Autostart = new AutostartInfo { State = state, Path = status.Path } };
    }

    private EngineStatus Status()
    {
        var snapshot = config();
        var shell = Shell;
        return new EngineStatus
        {
            Version = info.Version,
            ProcessId = info.ProcessId,
            ConfigPath = info.ConfigPath,
            ShortcutsPaused = shell.ShortcutsPaused,
            Running = [.. launcher.RunningFlowIds.Order(StringComparer.Ordinal)],
            ShortcutProblems = [.. shell.ShortcutProblems],
            Errors = snapshot.Problems.Count(p => p.Severity == Severity.Error),
            Warnings = snapshot.Problems.Count(p => p.Severity == Severity.Warning),
        };
    }

    /// <summary>By id, else by name (case-insensitive), as typed on the command line.</summary>
    internal FlowState? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var flows = config().Flows;
        return flows.FirstOrDefault(f => f.FlowId == text)
            ?? flows.FirstOrDefault(f => string.Equals(NameOf(f), text, StringComparison.OrdinalIgnoreCase));
    }

    private static bool SameFile(string a, string b)
    {
        try
        {
            return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>By id only (a desktop shortcut, D-049).</summary>
    private FlowState? FindById(string? id) =>
        string.IsNullOrEmpty(id) ? null : config().Flows.FirstOrDefault(f => f.FlowId == id);

    private static string NameOf(FlowState state) => (state.Running ?? state.Written)?.Name ?? state.FlowId;

    private static PipeMessage NotFound(int id, string? text) =>
        PipeMessage.Fail(id, string.IsNullOrWhiteSpace(text) ? "Say which flow." : $"There's no flow called \"{text}\".");

    private FlowInfo Describe(FlowState state)
    {
        var flow = state.Running ?? state.Written;
        return new FlowInfo
        {
            Id = state.FlowId,
            Name = NameOf(state),
            Runnable = state.Running is { Enabled: true },
            Stale = state.IsStale,
            Enabled = flow?.Enabled ?? false,
            Running = launcher.IsRunning(state.FlowId),
            Hotkeys = flow is null ? [] : [.. flow.Hotkeys],
            Errors = state.Problems.Count(p => p.Severity == Severity.Error),
            HeldBack = HeldBackSinceLastRun(state.FlowId),
        };
    }

    /// <summary>The guard's latest note for the flow, unless a run started after it (then it is old news).</summary>
    private string? HeldBackSinceLastRun(string flowId)
    {
        if (Guard?.LastHeldBack(flowId) is not { } note)
            return null;
        var lastRun = history?.Recent.Where(r => r.FlowId == flowId).Select(r => (DateTimeOffset?)r.Started).Max();
        return lastRun is { } started && started >= note.At ? null : note.Sentence;
    }

    private static ProblemInfo Describe(Problem problem) => new()
    {
        Severity = problem.Severity == Severity.Error ? "error" : "warning",
        Path = problem.Path,
        Message = problem.Message,
        FlowId = problem.FlowId,
    };

    private sealed class DryProvider(string id) : IStepProvider
    {
        public string Id => id;

        public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
        {
            // A dry run never shows a window (DESIGN §3.2): it only says what it would have shown.
            if (context.Node.Step == ProviderCatalog.MenuShowId)
            {
                var count = MenuProvider.IdsOf(context.Node).Count;
                context.SetSummary($"Would show {count} {(count == 1 ? "choice" : "choices")}");
                return Task.CompletedTask;
            }
            if (context.Node.Step == ProviderCatalog.StopFlowId)
            {
                context.SetSummary(StopProvider.DrySummary(context.Node));
                return Task.CompletedTask;
            }
            if (context.Node.Step == ProviderCatalog.PromptAskId)
            {
                context.SetSummary(PromptProvider.DrySummary(context.Node));
                return Task.CompletedTask;
            }
            if (context.Node.Step == ProviderCatalog.PromptPickId)
            {
                try
                {
                    context.SetSummary(PickProvider.DryRun(context));
                }
                catch (StepFailedException e)
                {
                    return Task.FromException(e);
                }
                return Task.CompletedTask;
            }
            foreach (var spec in context.Descriptor.Params.Where(p => p.Kind == ParamKind.LibraryRef && p.Library is not null))
            {
                var key = context.Node.With.TryGetValue(spec.Name, out var value) && value is ScalarParam s ? s.Text : "";
                if (PickResults.StepIn(key) is not null)
                {
                    // Another step's device (D-130), known only when it really runs.
                    context.SetSummary($"Would use {PickResults.Describe(context.Flow, key) ?? key}");
                    continue;
                }
                if (key.Length == 0 && spec.Required)
                    return Task.FromException(new StepFailedException("Choose a device."));
                if (key.Length > 0 && !context.Library.Contains(spec.Library!.Value, key))
                    return Task.FromException(new StepFailedException(Library.Missing(spec.Library!.Value, key)));
            }
            return Task.CompletedTask;
        }
    }
}
