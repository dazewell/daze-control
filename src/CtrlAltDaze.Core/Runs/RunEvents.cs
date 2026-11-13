namespace CtrlAltDaze.Core.Runs;

/// <summary>Live progress of one run, for the pop-up, the editor's run overlay and the pipe (FEATURES F5).</summary>
public abstract record RunEvent(Guid RunId, string FlowId, DateTimeOffset At)
{
    /// <summary>Set on a nested run's events (a <c>flow.run</c> step that waits, D-062): the run that called it.</summary>
    public Guid? ParentRunId { get; init; }

    /// <summary>With <see cref="ParentRunId"/>: the calling step's id.</summary>
    public string? ParentNodeId { get; init; }
}

public sealed record RunStarted(Guid RunId, string FlowId, DateTimeOffset At, int NodeCount) : RunEvent(RunId, FlowId, At)
{
    /// <summary>The version that runs (in process only; not sent on the pipe). A reload can't change it mid-run.</summary>
    public Model.Flow? Flow { get; init; }
}

public sealed record NodeStarted(Guid RunId, string FlowId, DateTimeOffset At, string NodeId, int Attempt)
    : RunEvent(RunId, FlowId, At);

/// <summary>Also raised for nodes that never ran (Skipped, Cancelled), so every node ends with one.</summary>
public sealed record NodeFinished(Guid RunId, string FlowId, DateTimeOffset At, NodeResult Result) : RunEvent(RunId, FlowId, At);

public sealed record RunFinished(Guid RunId, string FlowId, DateTimeOffset At, FlowOutcome Outcome) : RunEvent(RunId, FlowId, At)
{
    /// <summary>The user ended the run on purpose (a menu closed with nothing picked, D-054): no pop-up, no tray problem.</summary>
    public bool Quiet { get; init; }

    /// <summary>In process only (not sent on the pipe): the flow's name when it ended, for <c>flow.finished</c> events (T26.3).</summary>
    public string? FlowName { get; init; }

    /// <summary>In process only: the flows that started this run (a start-mode call or a flow event), top first; empty = a person or the OS.</summary>
    public IReadOnlyList<string> Chain { get; init; } = [];
}

/// <summary>One finished run as kept in the history (last 100, <c>history.jsonl</c>).</summary>
/// <param name="RunId">Same id as the run's events.</param>
/// <param name="FlowId">The flow's YAML key.</param>
/// <param name="FlowName">Its name at the time, for the Runs page after a rename.</param>
/// <param name="Trigger">"Alt+Z", "Tray", "Command line", "Editor".</param>
/// <param name="Started">Wall-clock start (UTC).</param>
/// <param name="Duration">Start to finish.</param>
/// <param name="Outcome">How the run ended.</param>
/// <param name="Nodes">One result per node, in flow order.</param>
public sealed record RunRecord(Guid RunId, string FlowId, string FlowName, string Trigger, DateTimeOffset Started,
    TimeSpan Duration, FlowOutcome Outcome, NodeResult[] Nodes);
