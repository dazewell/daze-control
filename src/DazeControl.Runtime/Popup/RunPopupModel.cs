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
/// <param name="Progress">Finished nodes / all nodes, 0–1.</param>
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

    /// <summary>What to show now; null = nothing (no run yet, or the run asked for no pop-up).</summary>
    public PopupView? Current { get; private set; }

    /// <param name="e">Any run event.</param>
    /// <param name="flow">The definition the run uses (for names); only read on <see cref="RunStarted"/>, and
    /// <see cref="RunStarted.Flow"/> wins when set.</param>
    /// <param name="settings">Duration setting, read when the run finishes.</param>
    /// <returns>True when <see cref="Current"/> changed and the pop-up should redraw.</returns>
    public bool Apply(RunEvent e, Flow? flow, PopupSettings settings)
    {
        if (e is RunStarted started)
        {
            flow = started.Flow ?? flow;
            if (flow is null || !flow.Popup)
                return false;
            _runs[started.RunId] = new RunTrack(flow, started.NodeCount, started.At);
        }
        if (!_runs.TryGetValue(e.RunId, out var run))
            return false;
        switch (e)
        {
            case NodeStarted node:
                run.Running.Remove(node.NodeId);
                run.Running.Add(node.NodeId);
                break;
            case NodeFinished node:
                run.Running.Remove(node.Result.NodeId);
                run.Finished.Add(node.Result);
                break;
            case RunFinished finished:
                _runs.Remove(e.RunId);
                Current = Finish(e.RunId, e.FlowId, run, finished.Outcome, finished.At - run.Started, settings);
                return true;
        }
        Current = Progress(e.RunId, e.FlowId, run);
        return true;
    }

    private static PopupView Progress(Guid runId, string flowId, RunTrack run)
    {
        var done = run.Finished.Count;
        var detail = run.Running.Count switch
        {
            0 => done == 0 ? "Starting…" : "Working…",
            1 => run.NameOf(run.Running[0]) + "…",
            var n => $"{run.NameOf(run.Running[0])}… and {n - 1} more",
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
        var problem = run.Finished.FirstOrDefault(r => r.Outcome is NodeOutcome.Failed or NodeOutcome.FailedIgnored);
        return outcome switch
        {
            FlowOutcome.PartiallySucceeded => view with
            {
                Status = "",
                Headline = "Done, with a problem",
                Tone = PopupTone.Partial,
                Detail = problem is null
                    ? "Some steps were skipped."
                    : $"{run.NameOf(problem.NodeId)} didn't work, so it went on without it.",
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

    private sealed class RunTrack(Flow flow, int total, DateTimeOffset started)
    {
        public Flow Flow { get; } = flow;
        public int Total { get; } = Math.Max(total, 1);
        public DateTimeOffset Started { get; } = started;
        public List<string> Running { get; } = [];
        public List<NodeResult> Finished { get; } = [];

        public double Fraction => Math.Clamp((double)Finished.Count / Total, 0, 1);

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
