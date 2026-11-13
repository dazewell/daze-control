using System.Globalization;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Editor.ViewModels.Canvas;

namespace DazeControl.Editor.ViewModels.Runs;

/// <summary>How runs are put into words on the canvas and the Runs page (UX §5.7, the OSD's D-026 voice).</summary>
public static class RunWords
{
    /// <summary>"0.4 s", "1.2 s", "12 s".</summary>
    public static string Seconds(TimeSpan time) =>
        string.Create(CultureInfo.CurrentCulture, $"{Math.Round(time.TotalSeconds, time.TotalSeconds < 10 ? 1 : 0)} s");

    public static string FirstLine(string text) => text.ReplaceLineEndings("\n").Split('\n')[0].Trim();

    /// <summary>The result chip: "Done", "Done, with a problem", "Didn't finish", "Stopped".</summary>
    public static string Chip(FlowOutcome outcome) => outcome switch
    {
        FlowOutcome.Succeeded => "Done",
        FlowOutcome.PartiallySucceeded => "Done, with a problem",
        FlowOutcome.Failed => "Didn't finish",
        _ => "Stopped",
    };

    /// <summary>The run's result sentence (§5.7 #13, #14, #16, #48).</summary>
    /// <param name="title">A node id → the name the user knows it by.</param>
    /// <param name="flow">The flow as it is now, to tell a failure its fallback fixed; null when it's gone.</param>
    public static string Sentence(RunRecord run, Func<string, string> title, Flow? flow = null)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(title);
        if (run.Outcome == FlowOutcome.Succeeded && flow is not null && Outcomes.Fix(flow, run.Nodes) is { } fix)
            return $"{run.FlowName} worked. {Outcomes.FixSentence(flow, run.Nodes, fix, title)}";
        var failed = run.Nodes.FirstOrDefault(n => n.Outcome is NodeOutcome.Failed or NodeOutcome.FailedIgnored);
        var reason = failed?.Error is { } error ? " " + Sentence(FirstLine(error)) : "";
        return run.Outcome switch
        {
            FlowOutcome.Succeeded => $"{run.FlowName} is done.",
            FlowOutcome.PartiallySucceeded when failed is not null =>
                $"Done, with a problem. {title(failed.NodeId)} didn't work, so it went on without it.",
            FlowOutcome.PartiallySucceeded => "Done, with a problem. Some steps were skipped.",
            FlowOutcome.Failed => $"{run.FlowName} didn't finish.{reason}",
            _ => $"{run.FlowName} was stopped.",
        };
    }

    /// <summary>A pill's text once its step finished: its duration, or what happened.</summary>
    public static NodeRunView View(NodeResult node, bool showDuration = true, int order = 0)
    {
        ArgumentNullException.ThrowIfNull(node);
        var took = showDuration && node.Attempts > 0 ? " · " + Seconds(node.Duration) : "";
        return node.Outcome switch
        {
            NodeOutcome.Succeeded when node.TurnedOff == true => new(NodeRunState.Skipped, "Turned off", order), // D-148
            NodeOutcome.Succeeded when node.SkippedBecause is { Length: > 0 } why => new(NodeRunState.Succeeded, "Skipped: " + why, order, true),
            NodeOutcome.Succeeded => new(NodeRunState.Succeeded, showDuration ? Seconds(node.Duration) : "", order),
            NodeOutcome.Failed => new(NodeRunState.Failed, "Didn't work" + took, order),
            NodeOutcome.FailedIgnored => new(NodeRunState.FailedMayFail, "Didn't work" + took, order),
            NodeOutcome.Skipped => new(NodeRunState.Skipped, "Skipped", order),
            NodeOutcome.NotNeeded => new(NodeRunState.Skipped, "Not needed", order), // #47
            _ => new(NodeRunState.Skipped, "Stopped", order),
        };
    }

    private static string Sentence(string text) => text.Length > 0 && !text.EndsWith('.') ? text + "." : text;
}
