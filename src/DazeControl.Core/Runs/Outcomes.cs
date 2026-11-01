namespace DazeControl.Core.Runs;

/// <summary>How one node of a run ended (FEATURES F1).</summary>
public enum NodeOutcome
{
    Succeeded,
    Failed,

    /// <summary>Failed with policy <c>ignore</c> ("may fail"): counts as done.</summary>
    FailedIgnored,
    Skipped,
    Cancelled,
}

public enum FlowOutcome
{
    Succeeded,

    /// <summary>Finished, but something failed or was skipped on the way.</summary>
    PartiallySucceeded,
    Failed,
    Cancelled,
}

/// <param name="NodeId">The node's id in its flow.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Attempts">0 when the node never started (skipped / cancelled before it ran).</param>
/// <param name="Error">Plain-language reason for the pop-up and history; null on success.</param>
/// <param name="Duration">Wall time from first start to end, retries included.</param>
public sealed record NodeResult(string NodeId, NodeOutcome Outcome, int Attempts = 1, string? Error = null,
    TimeSpan Duration = default);

public static class Outcomes
{
    /// <param name="nodes">Outcomes of every node in the flow.</param>
    /// <param name="cancelled">The run was cancelled from outside (user, <c>whenRunning: restart</c>, exit).</param>
    /// <param name="stopped">A node failed with policy <c>stop</c> and ended the run.</param>
    public static FlowOutcome Summarize(IEnumerable<NodeOutcome> nodes, bool cancelled = false, bool stopped = false)
    {
        if (cancelled)
            return FlowOutcome.Cancelled;
        if (stopped)
            return FlowOutcome.Failed;
        var all = nodes.ToList();
        if (all.TrueForAll(o => o == NodeOutcome.Succeeded))
            return FlowOutcome.Succeeded;
        return all.Contains(NodeOutcome.Succeeded) ? FlowOutcome.PartiallySucceeded : FlowOutcome.Failed;
    }
}
