using DazeControl.Core.Model;

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

    /// <summary>A fallback step whose failure never came (D-065): counts neither as progress nor as a problem.
    /// Fires <c>always</c> edges like <see cref="Skipped"/>.</summary>
    NotNeeded,
}

public enum FlowOutcome
{
    Succeeded,

    /// <summary>Finished, but something failed or was skipped on the way (not counting what a fallback fixed).</summary>
    PartiallySucceeded,
    Failed,
    Cancelled,
}

/// <param name="NodeId">The node's id in its flow.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Attempts">0 when the node never started (skipped / cancelled before it ran).</param>
/// <param name="Error">Plain-language reason for the pop-up and history; null on success.</param>
/// <param name="Duration">Wall time from first start to end, retries included.</param>
/// <param name="Start">When it started, from the run's start (the Runs timeline); zero when it never started, and in
/// runs recorded before 0.15.</param>
/// <param name="Order">1 for the first step started in the run, 2 for the next…; 0 when it never started (and before 0.15).</param>
/// <param name="Retries">When each try after the first started, from the run's start (the timeline's notches).</param>
public sealed record NodeResult(string NodeId, NodeOutcome Outcome, int Attempts = 1, string? Error = null,
    TimeSpan Duration = default, TimeSpan Start = default, int Order = 0, TimeSpan[]? Retries = null);

public static class Outcomes
{
    /// <summary>Without fallbacks: no failure counts as handled.</summary>
    public static FlowOutcome Summarize(IEnumerable<NodeOutcome> nodes, bool cancelled = false, bool stopped = false) =>
        Summarize(nodes.Select(o => (o, false)), cancelled, stopped);

    /// <param name="nodes">Outcomes of every node in the flow; <c>Handled</c> marks a failure its fallback took
    /// over (the unit has a failure edge), or a skip that only follows from such a failure (D-065).</param>
    /// <param name="cancelled">The run was cancelled from outside (user, <c>whenRunning: restart</c>, exit).</param>
    /// <param name="stopped">A node failed with policy <c>stop</c> and ended the run.</param>
    public static FlowOutcome Summarize(IEnumerable<(NodeOutcome Outcome, bool Handled)> nodes, bool cancelled = false,
        bool stopped = false)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (cancelled)
            return FlowOutcome.Cancelled;
        if (stopped)
            return FlowOutcome.Failed;
        var all = nodes.ToList();
        if (all.TrueForAll(n => n.Outcome is NodeOutcome.Succeeded or NodeOutcome.NotNeeded
                || (n.Handled && n.Outcome is NodeOutcome.Failed or NodeOutcome.FailedIgnored or NodeOutcome.Skipped)))
            return FlowOutcome.Succeeded;
        return all.Exists(n => n.Outcome == NodeOutcome.Succeeded) ? FlowOutcome.PartiallySucceeded : FlowOutcome.Failed;
    }

    /// <summary>
    /// A failure its fallback fixed, for "Connect Technics didn't work, so Connect Sony ran instead." (#48): the first
    /// handled failure (in start order) and the first step of its fallback that started. Null when nothing failed,
    /// nothing handled it, or no fallback started.
    /// </summary>
    /// <param name="flow">The flow the results belong to (its edges say what handled what).</param>
    /// <param name="results">The run's node results.</param>
    public static (string Failed, string Fallback)? Fix(Flow flow, IEnumerable<NodeResult> results)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(results);
        var byId = new Dictionary<string, NodeResult>(StringComparer.Ordinal);
        foreach (var r in results)
            byId[r.NodeId] = r;
        var handled = FlowShape.WithFallback(flow);
        var failures = byId.Values
            .Where(r => r.Outcome is NodeOutcome.Failed or NodeOutcome.FailedIgnored && handled.Contains(r.NodeId)
                && flow.FindNode(r.NodeId) is StepNode)
            .OrderBy(r => r.Order == 0 ? int.MaxValue : r.Order);
        foreach (var failure in failures)
        {
            var unit = FlowShape.UnitOf(flow, failure.NodeId);
            var started = flow.Nodes
                .Where(n => n.After.Any(e => e.From == unit && e.When == EdgeWhen.Failure))
                .SelectMany(n => n is GroupNode card ? card.Members.AsEnumerable() : [n.Id])
                .Select(id => byId.GetValueOrDefault(id))
                .Where(r => r is { Order: > 0 })
                .OrderBy(r => r!.Order)
                .FirstOrDefault();
            if (started is not null)
                return (failure.NodeId, started.NodeId);
        }
        return null;
    }
}
