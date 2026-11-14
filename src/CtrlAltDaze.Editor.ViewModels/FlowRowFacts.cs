using System.Globalization;
using CtrlAltDaze.Core.Runs;

namespace CtrlAltDaze.Editor.ViewModels;

/// <summary>The words on a Flows list row (flow-list DESIGN Part D): pure, so the times are tested without a clock.</summary>
public static class FlowRowFacts
{
    /// <summary>First shortcut, "+1" when there are more; "No shortcut" without any.</summary>
    public static string Shortcut(IReadOnlyList<string> hotkeys) => hotkeys.Count switch
    {
        0 => "No shortcut",
        1 => hotkeys[0],
        _ => $"{hotkeys[0]} +{hotkeys.Count - 1}",
    };

    /// <summary>All the shortcuts, for the tooltip.</summary>
    public static string ShortcutTip(IReadOnlyList<string> hotkeys) =>
        hotkeys.Count == 0 ? "No shortcut" : string.Join(", ", hotkeys);

    /// <summary>The row's status dot: none without a record, a problem for failed or partly done runs, calm otherwise.</summary>
    public static FlowRowState State(RunRecord? run, bool running) =>
        running ? FlowRowState.Running
        : run is null ? FlowRowState.None
        : run.Outcome switch
        {
            FlowOutcome.Succeeded => FlowRowState.Done,
            FlowOutcome.PartiallySucceeded => FlowRowState.Warning,
            FlowOutcome.Failed => FlowRowState.Failed,
            _ => FlowRowState.None,
        };

    public static string Steps(int count) => count switch { 0 => "No steps", 1 => "1 step", _ => $"{count} steps" };

    /// <summary>"Used by 2 flows" (the Library's wording); empty when no flow calls this one.</summary>
    public static string UsedBy(int callers) => callers switch { 0 => "", 1 => "Used by 1 flow", _ => $"Used by {callers} flows" };

    /// <summary>
    /// "Done 2 h ago", "Didn't finish Mon 14:02"; "No recent run" without a record (the history keeps ≤ 200 runs, so
    /// "Not run yet" would be untrue). <paramref name="spoken"/> is for screen readers: "done 2 hours ago".
    /// </summary>
    public static string LastRun(RunRecord? run, DateTimeOffset now, bool spoken = false)
    {
        if (run is null)
            return "No recent run";
        var word = Runs.RunWords.Chip(run.Outcome);
        if (spoken)
            word = char.ToLowerInvariant(word[0]) + word[1..];
        var age = now - run.Started;
        if (age >= TimeSpan.FromDays(1))
        {
            var local = run.Started.ToLocalTime();
            var format = age < TimeSpan.FromDays(6) ? "ddd HH:mm" : "d MMM HH:mm";
            return $"{word} {local.ToString(format, CultureInfo.CurrentCulture)}";
        }
        return $"{word} {Ago(age, spoken)}";
    }

    private static string Ago(TimeSpan age, bool spoken)
    {
        if (age < TimeSpan.FromMinutes(1))
            return "just now";
        if (age < TimeSpan.FromHours(1))
            return spoken ? Plural((int)age.TotalMinutes, "minute") : $"{(int)age.TotalMinutes} min ago";
        return spoken ? Plural((int)age.TotalHours, "hour") : $"{(int)age.TotalHours} h ago";
    }

    private static string Plural(int n, string unit) => $"{n} {unit}{(n == 1 ? "" : "s")} ago";
}

/// <summary>What the last run of a flow looks like in the list (colour of the status dot).</summary>
public enum FlowRowState
{
    None,
    Running,
    Done,
    Warning,
    Failed,
}
