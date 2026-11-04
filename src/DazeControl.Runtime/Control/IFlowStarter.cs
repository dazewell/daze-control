using DazeControl.Core.Model;

namespace DazeControl.Runtime.Control;

/// <summary>A flow a <c>flow.run</c> step may run: its runnable version, or why there is none (DESIGN §2.3).</summary>
/// <param name="Flow">The version to run; null with <paramref name="Refusal"/>.</param>
/// <param name="Library">The library that version was validated with.</param>
/// <param name="Name">For messages: the flow's name, else its id.</param>
/// <param name="Refusal">Why it can't run (#51 missing, #33 never valid, #52 turned off); null when it can.</param>
public sealed record CalledFlow(Flow? Flow, Library Library, string Name, string? Refusal);

/// <summary>How a start-mode call went (DESIGN §2.2).</summary>
/// <param name="Started">False = the step fails with <paramref name="Message"/>.</param>
/// <param name="Message">Why it didn't start, or #58 when its <c>whenRunning: ignore</c> dropped the start.</param>
public sealed record StartAnswer(bool Started, string? Message = null);

/// <summary>What <c>flow.run</c> needs from the engine: the current config's flows and the launcher (D-061, D-062).</summary>
public interface IFlowStarter
{
    /// <summary>The called flow's runnable version in the current config.</summary>
    CalledFlow Resolve(string flowId);

    /// <summary>Start mode: starts the flow the way the tray does (own <c>whenRunning</c>, run record, pop-up).</summary>
    /// <param name="flowId">The flow to start.</param>
    /// <param name="trigger">For its history: "Flow · Headphones".</param>
    /// <param name="chain">The caller's call chain (flow ids from the top run), passed on so a start loop is caught.</param>
    /// <param name="inputs">What the caller passes (D-064); checked against the same resolution that is started.</param>
    /// <param name="callerName">For the message when a passed device isn't in the flow's library.</param>
    StartAnswer Start(string flowId, string trigger, IReadOnlyList<string> chain, IReadOnlyDictionary<string, string> inputs, string callerName);
}

/// <summary>The engine's <see cref="IFlowStarter"/>: flows from the latest snapshot, starts through the launcher.</summary>
/// <param name="snapshot">The config now (<c>ConfigHost.Current</c>).</param>
public sealed class FlowCalls(Func<ConfigSnapshot> snapshot) : IFlowStarter
{
    /// <summary>Set once the launcher exists (it needs the runner, which needs this).</summary>
    public FlowLauncher? Launcher { get; set; }

    /// <summary>Most flows deep a call chain may be (#54, D-063).</summary>
    public const int MaxDepth = 8;

    public CalledFlow Resolve(string flowId)
    {
        var state = snapshot().Flows.FirstOrDefault(f => f.FlowId == flowId);
        if (state is null)
            return new(null, Library.Empty, flowId, $"{flowId} isn't there anymore. Pick another flow.");
        var name = state.Running?.Name ?? state.Written?.Name ?? flowId;
        // The file's On switch wins over a retained older version (D-021 keeps definitions, not "on"): §2.3.
        var enabled = (state.Written ?? state.Running)?.Enabled ?? false;
        return state.Running switch
        {
            _ when state.Written is { Enabled: false } || (state.Running is not null && !enabled) =>
                new(null, Library.Empty, name, $"{name} is turned off, so it didn't run. Turn it on in Flows."),
            null => new(null, Library.Empty, name, $"{name} didn't start. It needs fixing."),
            { Enabled: false } => new(null, Library.Empty, name, $"{name} is turned off, so it didn't run. Turn it on in Flows."),
            var flow => new(flow, state.Library, name, null),
        };
    }

    public StartAnswer Start(string flowId, string trigger, IReadOnlyList<string> chain, IReadOnlyDictionary<string, string> inputs, string callerName)
    {
        ArgumentNullException.ThrowIfNull(chain);
        ArgumentNullException.ThrowIfNull(inputs);
        var called = Resolve(flowId);
        if (called.Flow is not { } flow)
            return new(false, called.Refusal);
        if (Guard(chain, flowId, called.Name) is { } loop)
            return new(false, loop);
        if (FlowInputs.Check(flow, called.Library, inputs, callerName) is { } missing)
            return new(false, missing);
        if (Launcher is not { } launcher)
            return new(false, "Flows can't start other flows yet.");
        if (flow.WhenRunning == WhenRunning.Ignore && launcher.IsRunning(flowId))
            return new(true, $"{called.Name} was already running, so it wasn't started again.");
        // Resolved here, so the launched run (whose runner sees no caller) gets the caller's devices (D-064).
        var run = launcher.TriggerAsync(FlowInputs.Apply(flow, inputs), called.Library, trigger, chain);
        // The launched run is the launcher's: its result goes to history and the pop-up. Observe a fault only.
        _ = run.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return new(true);
    }

    /// <summary>The runtime loop guard (D-063): #53 when the flow is already on the chain, #54 past 8 deep.</summary>
    /// <param name="chain">Flow ids from the top run down to the caller.</param>
    public static string? Guard(IReadOnlyList<string> chain, string flowId, string name)
    {
        ArgumentNullException.ThrowIfNull(chain);
        if (chain.Contains(flowId))
            return $"Didn't run {name}: it's already running further up, so that would loop.";
        if (chain.Count >= MaxDepth)
            return $"Flows ran inside each other more than {MaxDepth} deep, so this one stopped.";
        return null;
    }
}
