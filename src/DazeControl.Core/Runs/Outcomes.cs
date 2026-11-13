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
    TimeSpan Duration = default, TimeSpan Start = default, int Order = 0, TimeSpan[]? Retries = null)
{
    /// <summary>A <c>flow.run</c> step that waited: the nested run it made (its last try), recursively (D-062).
    /// Null for every other step, and in runs recorded before 0.28.</summary>
    public CalledRun? Called { get; init; }

    /// <summary>What the step says about itself ("Picked Couch TV", M12): shown beside its result. Null = nothing to add.</summary>
    public string? Summary { get; init; }

    /// <summary>Set when "Only if" wasn't met and the step was skipped (D-081): why, in the probe's words ("Technics is
    /// connected"). The outcome is Succeeded; readers prefix "Skipped: ". Null otherwise and in runs recorded before 0.41.</summary>
    public string? SkippedBecause { get; init; }

    /// <summary>
    /// A check step failed because it read its fact and the fact isn't as asked (T25.1), not because it couldn't read it.
    /// In-process only (the "Only run if" guard): never written to history or sent over the pipe.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool NotMet { get; init; }

    /// <summary>True when the step is turned off (<c>enabled: false</c>, D-148) and ended Succeeded without running.
    /// Readers say "Turned off". Null otherwise and in runs recorded before T25.4.</summary>
    public bool? TurnedOff { get; init; }

    /// <summary>The values the step gave that a later setting uses (T25.5, D-145): masked, cut, Sensitive ones hidden. Null
    /// when none, in a dry run, and in runs recorded before this was added.</summary>
    public ShownValue[]? Values { get; init; }

    /// <summary>The settings whose text changed when values were filled in, as the step really got them (T25.5). Same rules
    /// as <see cref="Values"/>.</summary>
    public ShownValue[]? Used { get; init; }

    /// <summary>Succeeded, but the flow it ran ended with a problem: a problem for the caller (DESIGN §2.2).</summary>
    public bool IsPartialCall => Outcome == NodeOutcome.Succeeded && Called?.Outcome == FlowOutcome.PartiallySucceeded;
}

/// <summary>The nested run of a <c>flow.run</c> step (D-062), kept inside the caller's history record.</summary>
/// <param name="RunId">Same id as the nested run's events.</param>
/// <param name="FlowId">The called flow's YAML key.</param>
/// <param name="FlowName">Its name at the time.</param>
/// <param name="Outcome">How the nested run ended.</param>
/// <param name="Nodes">One result per node of the called flow, in flow order.</param>
public sealed record CalledRun(Guid RunId, string FlowId, string FlowName, FlowOutcome Outcome, NodeResult[] Nodes);

public static class Outcomes
{
    /// <summary>Without fallbacks: no failure counts as handled.</summary>
    public static FlowOutcome Summarize(IEnumerable<NodeOutcome> nodes, bool cancelled = false, bool stopped = false) =>
        Summarize(nodes.Select(o => (o, false)), cancelled, stopped);

    /// <param name="nodes">Outcomes of every node in the flow; <c>Handled</c> marks a failure its fallback took
    /// over (the unit has a failure edge), or a skip that only follows from such a failure (D-065).</param>
    /// <param name="cancelled">The run was cancelled from outside (user, <c>whenRunning: restart</c>, exit).</param>
    /// <param name="stopped">A node failed with policy <c>stop</c> and ended the run.</param>
    /// <param name="partialCall">A <c>flow.run</c> step succeeded but the flow it ran had a problem
    /// (<see cref="NodeResult.IsPartialCall"/>): the run ends PartiallySucceeded at best (DESIGN §2.2).</param>
    public static FlowOutcome Summarize(IEnumerable<(NodeOutcome Outcome, bool Handled)> nodes, bool cancelled = false,
        bool stopped = false, bool partialCall = false)
    {
        var outcome = SummarizeNodes(nodes, cancelled, stopped);
        return outcome == FlowOutcome.Succeeded && partialCall ? FlowOutcome.PartiallySucceeded : outcome;
    }

    private static FlowOutcome SummarizeNodes(IEnumerable<(NodeOutcome Outcome, bool Handled)> nodes, bool cancelled, bool stopped)
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

    /// <summary>A check step (D-081, D-118; <see cref="Catalog.ProviderCatalog.Checks"/>). Unmet it fails, so its words are the probe's, not "didn't work".</summary>
    public static bool IsCheck(string stepId) => Catalog.ProviderCatalog.Checks.Contains(stepId);

    /// <summary>
    /// #48 for a <see cref="Fix"/>: "Connect Technics didn't work, so Connect Sony ran instead."; for a check the probe's
    /// own words lead: "Technics isn't connected, so Speakers ran instead."
    /// </summary>
    public static string FixSentence(Flow flow, IEnumerable<NodeResult> results, (string Failed, string Fallback) fix, Func<string, string> title)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(title);
        if (flow.FindNode(fix.Failed) is StepNode { } step && IsCheck(step.Step)
            && results.FirstOrDefault(r => r.NodeId == fix.Failed)?.Error is { } error
            && error.ReplaceLineEndings("\n").Split('\n')[0].Trim().TrimEnd('.') is { Length: > 0 } why)
            return $"{why}, so {title(fix.Fallback)} ran instead.";
        return $"{title(fix.Failed)} didn't work, so {title(fix.Fallback)} ran instead.";
    }

    /// <summary>
    /// A failure its fallback fixed, for "Connect Technics didn't work, so Connect Sony ran instead." (#48): the first
    /// of <see cref="Fixes"/>. Null when nothing failed, nothing handled it, or no fallback started.
    /// </summary>
    public static (string Failed, string Fallback)? Fix(Flow flow, IEnumerable<NodeResult> results) =>
        Fixes(flow, results).Select(f => ((string, string)?)f).FirstOrDefault();

    /// <summary>
    /// Failed steps (in start order) whose fallback started, each with the first of its fallback's steps that did.
    /// Only <see cref="NodeOutcome.Failed"/>: a may-fail step also fired its success edges, so its fallback ran beside
    /// the normal branch, not instead of it.
    /// </summary>
    /// <param name="flow">The flow the results belong to (its edges say what handled what).</param>
    /// <param name="results">The run's node results.</param>
    public static IEnumerable<(string Failed, string Fallback)> Fixes(Flow flow, IEnumerable<NodeResult> results)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(results);
        var byId = new Dictionary<string, NodeResult>(StringComparer.Ordinal);
        foreach (var r in results)
            byId[r.NodeId] = r;
        var handled = FlowShape.WithFallback(flow);
        var failures = byId.Values
            .Where(r => r.Outcome == NodeOutcome.Failed && handled.Contains(r.NodeId)
                && flow.FindNode(r.NodeId) is StepNode)
            .OrderBy(r => r.Order == 0 ? int.MaxValue : r.Order);
        foreach (var failure in failures)
        {
            var unit = FlowShape.UnitOf(flow, failure.NodeId);
            var started = flow.Nodes
                .Where(n => n.After.Any(e => e.From == unit && e.When == EdgeWhen.Failure))
                .SelectMany(n => n is GroupNode card ? card.Members.AsEnumerable() : [n.Id])
                .Select(id => byId.GetValueOrDefault(id))
                .Where(r => r is { Order: > 0 } or { Attempts: > 0 })
                .OrderBy(r => r!.Order == 0 ? int.MaxValue : r.Order)
                .FirstOrDefault();
            if (started is not null)
                yield return (failure.NodeId, started.NodeId);
        }
    }
}
