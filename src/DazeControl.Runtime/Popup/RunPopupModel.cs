using System.Globalization;
using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;

namespace DazeControl.Runtime.Popup;

/// <summary>What the pop-up's glyph and colours say (UX §5.3 run states: shape + glyph, never colour alone).</summary>
public enum PopupTone
{
    Running,
    Succeeded,

    /// <summary>"Done, with a problem": caution, not red.</summary>
    Partial,
    Failed,

    /// <summary>Cancelled from outside; neutral.</summary>
    Stopped,
}

/// <summary>One frame of the run pop-up (UX §5.6).</summary>
/// <param name="Title">The flow's name.</param>
/// <param name="Status">Right of the name, short: "2 of 3" while running, "Done" on success, else empty.</param>
/// <param name="Headline">Row 3's first line in the tone's colour when the outcome needs more words than the
/// status column holds ("Done, with a problem", "Didn't finish"); null otherwise.</param>
/// <param name="Progress">Finished nodes / nodes counted, 0–1 (fallbacks count once they start, D-065).</param>
/// <param name="Detail">Row 3: the current step while running, then the outcome in plain words (≤ 2 lines).</param>
/// <param name="Hint">Row 4, failure only: "Select to see what happened".</param>
/// <param name="HideAfter">Null while running; how long the finished pop-up stays.</param>
/// <param name="Clickable">False = click-through. Only a failure takes clicks (and still never activates).</param>
public sealed record PopupView(Guid RunId, string FlowId, string Title, string Status, string? Headline, double Progress,
    string Detail, string? Hint, PopupTone Tone, TimeSpan? HideAfter, bool Clickable);

/// <summary>
/// Turns run events into what the pop-up shows. Pure: the engine feeds it on the message thread and draws
/// <see cref="Current"/>. Several runs at once → the one that changed last is shown.
/// </summary>
public sealed class RunPopupModel
{
    /// <summary>A failure stays at least this long so it can be read and selected (UX §5.6).</summary>
    public static readonly TimeSpan FailureMinimum = TimeSpan.FromSeconds(6);

    private readonly Dictionary<Guid, RunTrack> _runs = [];

    /// <summary>Nested runs (D-062) by RunId: the top run and its call step they fold into.</summary>
    private readonly Dictionary<Guid, (Guid Top, string Call, RunTrack Track)> _nested = [];

    /// <summary>What to show now; null = nothing (no run yet, or the run asked for no pop-up).</summary>
    public PopupView? Current { get; private set; }

    /// <param name="e">Any run event.</param>
    /// <param name="flow">The definition the run uses (for names); only read on <see cref="RunStarted"/>, and
    /// <see cref="RunStarted.Flow"/> wins when set.</param>
    /// <param name="settings">Duration setting, read when the run finishes.</param>
    /// <returns>True when <see cref="Current"/> changed and the pop-up should redraw.</returns>
    public bool Apply(RunEvent e, Flow? flow, PopupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(e);
        if (e.ParentRunId is { } parent)
            return ApplyNested(e, parent);
        if (e is RunStarted started)
        {
            flow = started.Flow ?? flow;
            // A menu is its own window, so a flow that only shows a menu has no pop-up (M12, DESIGN §3.2).
            if (flow is null || !flow.Popup || flow.Nodes.Count > 0 && flow.Nodes.All(n => n is StepNode { Step: ProviderCatalog.MenuShowId }))
                return false;
            _runs[started.RunId] = new RunTrack(flow, started.At);
            if (flow.Nodes.Any(n => n is StepNode { Step: ProviderCatalog.MenuShowId }))
                return false; // the pop-up comes with the first step that isn't the menu
        }
        if (!_runs.TryGetValue(e.RunId, out var run))
            return false;
        switch (e)
        {
            case NodeStarted node:
                run.Count(node.NodeId);
                if (run.IsMenu(node.NodeId))
                    return false;
                run.Running.Remove(node.NodeId);
                run.Running.Add(node.NodeId);
                break;
            case NodeFinished node:
                run.Running.Remove(node.Result.NodeId);
                run.Finished.Add(node.Result);
                if (run.IsMenu(node.Result.NodeId))
                    return false;
                break;
            case RunFinished finished:
                _runs.Remove(e.RunId);
                if (finished.Quiet)
                {
                    // Ended on purpose (a menu closed with nothing picked): nothing to say. A pop-up this run
                    // already showed goes; the shell hides its window when Current is null (T12.7).
                    var own = Current?.RunId == e.RunId;
                    if (own)
                        Current = null;
                    return own;
                }
                Current = Finish(e.RunId, e.FlowId, run, finished.Outcome, finished.At - run.Started, settings);
                return true;
        }
        Current = Progress(e.RunId, e.FlowId, run);
        return true;
    }

    /// <summary>
    /// Only the top run has a pop-up: a nested run's current step is the call step's detail ("Make it the default ·
    /// Connecting Sony…", #59) and its progress moves the bar within the call step's share.
    /// </summary>
    private bool ApplyNested(RunEvent e, Guid parent)
    {
        (Guid Top, string Call)? owner = _runs.ContainsKey(parent) ? (parent, e.ParentNodeId ?? "")
            : _nested.TryGetValue(parent, out var up) ? (up.Top, up.Call) : null;
        if (owner is not var (topId, call) || !_runs.TryGetValue(topId, out var top))
            return false;
        if (e is RunStarted { Flow: { } calledFlow } started)
        {
            _nested[started.RunId] = (topId, call, new RunTrack(calledFlow, started.At));
            top.CalledFlows[started.RunId] = calledFlow;
        }
        if (!_nested.TryGetValue(e.RunId, out var nested))
            return false;
        var direct = parent == topId;
        switch (e)
        {
            case NodeStarted node:
                nested.Track.Count(node.NodeId);
                if (!nested.Track.IsMenu(node.NodeId)) // the menu has its own window
                    top.NestedDetail[call] = $"{nested.Track.Flow.Name} · {nested.Track.NameOf(node.NodeId)}…";
                break;
            case NodeFinished node:
                nested.Track.Finished.Add(node.Result);
                if (direct)
                    top.NestedFraction[call] = nested.Track.Fraction;
                break;
            case RunFinished:
                _nested.Remove(e.RunId);
                if (direct)
                {
                    top.NestedDetail.Remove(call);
                    top.NestedFraction.Remove(call);
                }
                break;
        }
        Current = Progress(topId, top.Flow.Id, top);
        return true;
    }

    /// <summary>
    /// A desktop shortcut's run that didn't start (D-049): nobody sees the pipe's answer, so the pop-up says it. Shown as
    /// a failure that stays readable; not clickable (there's no run to open).
    /// </summary>
    public PopupView Refused(string flowId, string title, string message, PopupSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var duration = TimeSpan.FromSeconds(settings.Seconds);
        Current = new PopupView(Guid.Empty, flowId, title, "", "Didn't start", 0, message, null, PopupTone.Failed,
            duration > FailureMinimum ? duration : FailureMinimum, false);
        return Current;
    }

    private static PopupView Progress(Guid runId, string flowId, RunTrack run)
    {
        var done = run.Done;
        var detail = run.Running.Count switch
        {
            0 => done == 0 ? "Starting…" : "Working…",
            1 => run.Current(run.Running[0]),
            var n => $"{run.Current(run.Running[0])} and {n - 1} more",
        };
        return new PopupView(runId, flowId, run.Flow.Name, $"{done} of {run.Total}", null, run.Fraction, detail, null,
            PopupTone.Running, null, false);
    }

    private static PopupView Finish(Guid runId, string flowId, RunTrack run, FlowOutcome outcome, TimeSpan took,
        PopupSettings settings)
    {
        var duration = TimeSpan.FromSeconds(settings.Seconds);
        var view = new PopupView(runId, flowId, run.Flow.Name, "Done", null, 1, "Took " + Seconds(took), null,
            PopupTone.Succeeded, duration, false);
        var problem = run.Finished.FirstOrDefault(r => r.Outcome is NodeOutcome.Failed or NodeOutcome.FailedIgnored || r.IsPartialCall);
        return outcome switch
        {
            // A failure its fallback fixed is the plan working: "Done", in the normal tone (#48, D-065).
            FlowOutcome.Succeeded when Outcomes.Fix(run.Flow, run.Finished) is { } fix => view with
            {
                Detail = Outcomes.FixSentence(run.Flow, run.Finished, fix, run.NameOf),
            },
            FlowOutcome.PartiallySucceeded => view with
            {
                Status = "",
                Headline = "Done, with a problem",
                Tone = PopupTone.Partial,
                Detail = problem is null ? "Some steps were skipped." : Problem(run, problem),
            },
            FlowOutcome.Failed => view with
            {
                Status = "",
                Headline = "Didn't finish",
                Tone = PopupTone.Failed,
                Progress = run.Fraction,
                Detail = (run.Finished.FirstOrDefault(r => r.Outcome == NodeOutcome.Failed) ?? problem) is { } failure
                    ? Reason(run, failure)
                    : "A step didn't work.",
                Hint = "Select to see what happened",
                HideAfter = duration > FailureMinimum ? duration : FailureMinimum,
                Clickable = true,
            },
            FlowOutcome.Cancelled => view with
            {
                Status = "",
                Headline = "Stopped",
                Tone = PopupTone.Stopped,
                Progress = run.Fraction,
                Detail = "It was stopped before it finished.",
            },
            _ => view,
        };
    }

    /// <summary>"Connect didn't work, so it went on without it." (#14); a call names its flow first, recursively.</summary>
    private static string Problem(RunTrack run, NodeResult problem)
    {
        if (!problem.IsPartialCall)
            return $"{run.NameOf(problem.NodeId)} didn't work, so it went on without it.";
        var called = problem.Called!;
        var inner = called.Nodes.FirstOrDefault(r => r.Outcome is NodeOutcome.Failed or NodeOutcome.FailedIgnored || r.IsPartialCall);
        var track = new RunTrack(run.CalledFlows.GetValueOrDefault(called.RunId) ?? new Flow(called.FlowId, called.FlowName, []), run.Started);
        foreach (var flow in run.CalledFlows)
            track.CalledFlows[flow.Key] = flow.Value;
        return $"{called.FlowName}: " + (inner is null ? "some steps were skipped." : Problem(track, inner));
    }

    /// <summary>The first failure in plain words: the provider's reason, or "X didn't work." without one.</summary>
    private static string Reason(RunTrack run, NodeResult failure) =>
        string.IsNullOrWhiteSpace(failure.Error) ? $"{run.NameOf(failure.NodeId)} didn't work." : failure.Error.Trim();

    /// <summary>"3.1 s", "45 s", "2 min 5 s" (§5.7 #11).</summary>
    public static string Seconds(TimeSpan took)
    {
        if (took < TimeSpan.Zero)
            took = TimeSpan.Zero;
        if (took.TotalSeconds < 10)
            return took.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s";
        if (took.TotalSeconds < 60)
            return ((int)Math.Round(took.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + " s";
        return $"{(int)took.TotalMinutes} min {took.Seconds} s";
    }

    /// <summary>
    /// "n of m" counts the flow's nodes except fallbacks, which join m when one of their steps starts (D-065): a
    /// fallback that wasn't needed counts for nothing.
    /// </summary>
    private sealed class RunTrack
    {
        private readonly IReadOnlySet<string> _fallbacks;
        private readonly HashSet<string> _counted;

        public RunTrack(Flow flow, DateTimeOffset started)
        {
            Flow = flow;
            Started = started;
            _fallbacks = FlowShape.Fallbacks(flow);
            _counted = [.. flow.Nodes.Select(n => n.Id).Where(id => !_fallbacks.Contains(id))];
        }

        public Flow Flow { get; }
        public DateTimeOffset Started { get; }
        public List<string> Running { get; } = [];
        public List<NodeResult> Finished { get; } = [];

        /// <summary>Call step → its nested run's current step (#59).</summary>
        public Dictionary<string, string> NestedDetail { get; } = [];

        /// <summary>Call step → how far its nested run is (0–1).</summary>
        public Dictionary<string, double> NestedFraction { get; } = [];

        /// <summary>Flows run by calls, by nested RunId (names for the outcome).</summary>
        public Dictionary<Guid, Flow> CalledFlows { get; } = [];

        /// <summary>Row 3 while the step runs: its name, or the nested run's current step for a call.</summary>
        public string Current(string nodeId) => NestedDetail.TryGetValue(nodeId, out var nested) ? nested : NameOf(nodeId) + "…";

        public bool IsMenu(string nodeId) => Flow.FindNode(nodeId) is StepNode { Step: ProviderCatalog.MenuShowId };

        public int Total => Math.Max(_counted.Count, 1);

        public int Done => Finished.Count(r => _counted.Contains(r.NodeId));

        public double Fraction => Math.Clamp((Done + Running.Where(_counted.Contains).Sum(id => NestedFraction.GetValueOrDefault(id))) / Total, 0, 1);

        /// <summary>A step started: a fallback (and its card) counts from now on.</summary>
        public void Count(string stepId)
        {
            if (!_fallbacks.Contains(stepId))
                return;
            _counted.Add(stepId);
            _counted.Add(FlowShape.UnitOf(Flow, stepId));
        }

        /// <summary>The node's own name, else its provider's title, else its id.</summary>
        public string NameOf(string nodeId) => Flow.FindNode(nodeId) switch
        {
            { Name: { Length: > 0 } name } => name,
            StepNode step => ProviderCatalog.Find(step.Step)?.Title ?? nodeId,
            GroupNode => "Run together",
            _ => nodeId,
        };
    }
}

/// <summary>Where the pop-up goes in a work area (physical pixels): the OSD's six positions.</summary>
public static class PopupPlacement
{
    public const double MarginDip = 24;

    public static (int X, int Y) Place(int areaX, int areaY, int areaWidth, int areaHeight, int width, int height,
        PopupPosition position, int margin)
    {
        var left = areaX + margin;
        var right = areaX + areaWidth - width - margin;
        var centre = areaX + (areaWidth - width) / 2;
        var top = areaY + margin;
        var bottom = areaY + areaHeight - height - margin;
        return position switch
        {
            PopupPosition.TopCenter => (centre, top),
            PopupPosition.TopLeft => (left, top),
            PopupPosition.TopRight => (right, top),
            PopupPosition.BottomLeft => (left, bottom),
            PopupPosition.BottomRight => (right, bottom),
            _ => (centre, bottom),
        };
    }
}
