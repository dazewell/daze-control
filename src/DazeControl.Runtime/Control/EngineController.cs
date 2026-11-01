using DazeControl.Core.Catalog;
using DazeControl.Core.Runs;
using DazeControl.Core.Validation;
using DazeControl.Ipc;
using DazeControl.Runtime.Lifecycle;
using DazeControl.Runtime.Providers;

namespace DazeControl.Runtime.Control;

/// <summary>What only the message thread knows, published for the pipe (replaced whole, never mutated).</summary>
/// <param name="ShortcutProblems">"Alt+Z: Another app uses Alt+Z. Try another one."</param>
public sealed record ShellStatus(bool ShortcutsPaused, IReadOnlyList<string> ShortcutProblems)
{
    public static ShellStatus Empty { get; } = new(false, []);
}

/// <summary>Where the engine's files are and how it was started, for <c>status</c> and <c>autostart</c>.</summary>
public sealed record EngineInfo(string Version, int ProcessId, string ExePath, string ConfigPath);

/// <summary>
/// Every trigger and pipe request goes through here (FEATURES F2, F8; ARCHITECTURE → IPC): finds the flow by id or
/// name in the current config, starts it through the <see cref="FlowLauncher"/>, and answers the pipe ops. Thread-safe;
/// the shell's state arrives as <see cref="Shell"/> snapshots.
/// </summary>
public sealed class EngineController(Func<ConfigSnapshot> config, FlowLauncher launcher, RunHistory? history,
    FlowRunner dryRunner, Autostart autostart, EngineInfo info, Action<string> log)
{
    private volatile ShellStatus _shell = ShellStatus.Empty;

    public ShellStatus Shell
    {
        get => _shell;
        set => _shell = value;
    }

    /// <summary>A runner whose steps do nothing, for <c>dctl run --dry-run</c>: walks the flow's structure only.</summary>
    public static FlowRunner CreateDryRunner(TimeProvider time) =>
        new(ProviderCatalog.All.Select(d => new NoOpProvider(d.Id)), time);

    /// <summary>Starts a runnable flow (hotkey, tray, pipe); null when it can't run or the trigger was ignored.</summary>
    public Task<RunResult?> TriggerAsync(string flowId, string trigger) =>
        TriggerAsync(config().Flows.FirstOrDefault(f => f.FlowId == flowId), flowId, trigger);

    /// <summary>Starts this version of the flow (the one the caller checked), not whatever the config holds now.</summary>
    public Task<RunResult?> TriggerAsync(FlowState state, string trigger) => TriggerAsync(state, state.FlowId, trigger);

    private Task<RunResult?> TriggerAsync(FlowState? state, string flowId, string trigger)
    {
        if (state?.Running is not { Enabled: true } flow)
        {
            log($"run: {flowId} ({trigger}) not runnable");
            return Task.FromResult<RunResult?>(null);
        }
        log($"run: {flowId} triggered by {trigger}");
        var run = launcher.TriggerAsync(flow, state.Library, trigger);
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
            case Ops.Devices or Ops.TestStep or Ops.CaptureDisplayLayout:
                return PipeMessage.Fail(id, "This version can't do that yet: it arrives with the step types.");
            default:
                return PipeMessage.Fail(id, $"Unknown request \"{request.Op}\".");
        }
    }

    private async Task<PipeMessage> RunAsync(PipeRequest request, CancellationToken cancellationToken)
    {
        var id = request.Id;
        if (Find(request.Flow) is not { } state)
            return NotFound(id, request.Flow);
        if (state.Running is not { } flow)
            return PipeMessage.Fail(id, $"{NameOf(state)} needs fixing before it can run.");
        if (!flow.Enabled)
            return PipeMessage.Fail(id, $"{NameOf(state)} is turned off.");
        var trigger = request.Trigger ?? "Command line";

        if (request.DryRun)
        {
            var dry = await dryRunner.RunAsync(flow, state.Library, cancellationToken).ConfigureAwait(false);
            return new PipeMessage { Id = id, Ok = true, Run = Record(dry, flow.Id, flow.Name, trigger + " (dry run)") };
        }

        var run = TriggerAsync(state, trigger);
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
            : new PipeMessage { Id = id, Ok = true, Run = Record(result, flow.Id, flow.Name, trigger) };
    }

    private static RunRecord Record(RunResult result, string flowId, string name, string trigger) =>
        new(result.RunId, flowId, name, trigger, result.Started, result.Duration, result.Outcome, [.. result.Nodes]);

    /// <summary>A run keeps going when its flow leaves the config, so a raw id that is running can be cancelled.</summary>
    private PipeMessage Cancel(PipeRequest request)
    {
        var id = request.Id;
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
    /// History outlives the config: the filter matches a recorded flow id, then the config's id or name, then a
    /// recorded name.
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
                else if (runs.FirstOrDefault(r => string.Equals(r.FlowName, text, StringComparison.OrdinalIgnoreCase)) is { } byName)
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

    private PipeMessage Autostart(PipeRequest request)
    {
        if (request.Enable == true)
            autostart.Enable(info.ExePath);
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
        };
    }

    private static ProblemInfo Describe(Problem problem) => new()
    {
        Severity = problem.Severity == Severity.Error ? "error" : "warning",
        Path = problem.Path,
        Message = problem.Message,
        FlowId = problem.FlowId,
    };

    private sealed class NoOpProvider(string id) : IStepProvider
    {
        public string Id => id;

        public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
