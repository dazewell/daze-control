using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;

namespace DazeControl.Editor.ViewModels.Runs;

/// <summary>A bar on the timeline, in DIP from the timeline's top-left.</summary>
/// <param name="Notches">Where each retry started, in DIP from the bar's left edge.</param>
public sealed record TimelineBarView(string NodeId, string Title, NodeOutcome Outcome, double X, double Y, double Width,
    IReadOnlyList<double> Notches)
{
    /// <summary>A may-fail step that failed: dashed caution (UX §5.5).</summary>
    public bool IsMayFailFailed => Outcome == NodeOutcome.FailedIgnored;

    public string AutomationName { get; init; } = "";

    public override string ToString() => AutomationName;
}

/// <summary>A card's bracket over its members' lanes, in DIP.</summary>
public sealed record TimelineBracketView(string Title, double X, double Y, double Width, double Height);

/// <summary>A label on the time axis.</summary>
public sealed record TimelineTick(double X, string Text);

/// <summary>
/// The selected run (UX §5.5): its result sentence and the timeline — one row per lane (<see cref="RunTimeline"/>),
/// bars 20 DIP by state, cards as brackets, retries as notches, a bar at least 4 DIP wide. Picking a bar shows what
/// happened to that step.
/// </summary>
public sealed partial class RunDetailViewModel : ObservableObject
{
    public const double LaneHeight = 28, BarHeight = 20, MinBarWidth = 4, AxisHeight = 20, BarGap = 2;

    private readonly Dictionary<string, string> _titles;

    public RunDetailViewModel(RunRecord record, Flow? flow)
    {
        ArgumentNullException.ThrowIfNull(record);
        Record = record;
        Flow = flow;
        _titles = record.Nodes.ToDictionary(n => n.NodeId, n => TitleOf(flow?.FindNode(n.NodeId), n.NodeId), StringComparer.Ordinal);
        Timeline = RunTimeline.Build(record, flow);
        Sentence = RunWords.Sentence(record, NodeTitle);
        // Before 0.15 nothing recorded when steps started: every bar would sit at zero.
        HasTimeline = record.Nodes.Any(n => n.Order > 0);
        NotRun = [.. record.Nodes.Where(n => n.Attempts == 0 && flow?.FindNode(n.NodeId) is not GroupNode)
            .Select(n => $"{NodeTitle(n.NodeId)}: {(n.Outcome == NodeOutcome.Cancelled ? "stopped before it started" : "skipped")}")];
        Layout(600);
    }

    public RunRecord Record { get; }

    /// <summary>The flow as it is now; null when it was deleted (no brackets, "Open in editor" off).</summary>
    public Flow? Flow { get; }

    public RunTimeline Timeline { get; }

    public bool HasTimeline { get; }

    public bool CanOpenInEditor => Flow is not null;

    public string FlowName => Record.FlowName;

    /// <summary>"Connect Technics is done." (UX §5.7 #13, #14, #16).</summary>
    public string Sentence { get; }

    /// <summary>"Alt+Z · Today 14:02 · 3.1 s".</summary>
    public string Facts => string.Join(" · ", Record.Trigger, Record.Started.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
        RunWords.Seconds(Record.Duration));

    /// <summary>Steps that never ran: they have no bar.</summary>
    public IReadOnlyList<string> NotRun { get; }

    [ObservableProperty]
    public partial IReadOnlyList<TimelineBarView> Bars { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<TimelineBracketView> Brackets { get; private set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<TimelineTick> Ticks { get; private set; } = [];

    /// <summary>DIP, axis included.</summary>
    [ObservableProperty]
    public partial double Height { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedLines))]
    public partial TimelineBarView? SelectedBar { get; set; }

    /// <summary>What happened to the picked step: outcome, tries, time, its error.</summary>
    public IReadOnlyList<string> SelectedLines => SelectedBar is { } bar && Record.Nodes.FirstOrDefault(n => n.NodeId == bar.NodeId) is { } node
        ? Lines(node)
        : [];

    /// <summary>Lays the timeline out <paramref name="width"/> DIP wide (the view calls it when its width changes).</summary>
    public void Layout(double width)
    {
        width = Math.Max(width, 100);
        var length = Math.Max(Timeline.Length.TotalSeconds, 0.001);
        var scale = width / length; // DIP per second
        double X(TimeSpan t) => Math.Min(t.TotalSeconds * scale, width);
        var top = AxisHeight;
        var laneEnds = new Dictionary<int, double>();
        Bars = [.. Timeline.Bars.OrderBy(b => b.Start).Select(b =>
        {
            var x = X(b.Start);
            var w = Math.Max(X(b.End) - x, MinBarWidth);
            // A bar widened to MinBarWidth would cover the next instant step in its lane: start after it.
            if (laneEnds.TryGetValue(b.Lane, out var end) && x < end + BarGap)
                x = end + BarGap;
            if (x + w > width)
                x = Math.Max(0, width - w);
            laneEnds[b.Lane] = x + w;
            return new TimelineBarView(b.NodeId, NodeTitle(b.NodeId), b.Outcome, x, top + b.Lane * LaneHeight + (LaneHeight - BarHeight) / 2, w,
                [.. b.Notches.Select(n => Math.Clamp(X(n) - x, 0, w))])
            {
                AutomationName = $"{NodeTitle(b.NodeId)}, {Describe(b.Outcome)}, {RunWords.Seconds(b.End - b.Start)}",
            };
        })];
        Brackets = [.. Timeline.Brackets.Select(c => new TimelineBracketView(
            TitleOf(Flow?.FindNode(c.CardId), "Run together"), X(c.Start), top + c.FirstLane * LaneHeight + 2,
            Math.Max(X(c.End) - X(c.Start), MinBarWidth), (c.LastLane - c.FirstLane + 1) * LaneHeight - 4))];
        Ticks = [.. TickTimes(Timeline.Length).Select(t => new TimelineTick(X(t), RunWords.Seconds(t)))];
        Height = top + Math.Max(1, Timeline.Lanes) * LaneHeight;
    }

    /// <summary>About five round steps: 0, 0.5, 1 … s or 0, 2, 4 … s.</summary>
    internal static IEnumerable<TimeSpan> TickTimes(TimeSpan length)
    {
        var seconds = length.TotalSeconds;
        if (seconds <= 0)
            yield break;
        double[] steps = [0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300, 600];
        var step = steps.FirstOrDefault(s => seconds / s <= 6, 1200);
        for (var t = 0.0; t <= seconds + 1e-9; t += step)
            yield return TimeSpan.FromSeconds(Math.Round(t, 3));
    }

    private List<string> Lines(NodeResult node)
    {
        var lines = new List<string> { $"{NodeTitle(node.NodeId)}: {Describe(node.Outcome)}" };
        if (node.Attempts > 0)
            lines.Add(node.Attempts == 1
                ? $"Started at {RunWords.Seconds(node.Start)}, took {RunWords.Seconds(node.Duration)}."
                : $"Started at {RunWords.Seconds(node.Start)}, {node.Attempts} tries, took {RunWords.Seconds(node.Duration)}.");
        if (node.Error is { Length: > 0 } error)
            lines.AddRange(error.ReplaceLineEndings("\n").Split('\n').Where(l => l.Trim().Length > 0));
        return lines;
    }

    private static string Describe(NodeOutcome outcome) => outcome switch
    {
        NodeOutcome.Succeeded => "worked",
        NodeOutcome.Failed => "didn't work",
        NodeOutcome.FailedIgnored => "didn't work, the flow went on",
        NodeOutcome.Skipped => "skipped",
        _ => "stopped",
    };

    private string NodeTitle(string id) => _titles.GetValueOrDefault(id, id);

    private static string TitleOf(Node? node, string fallback) => node switch
    {
        StepNode step => step.Name ?? ProviderCatalog.Find(step.Step)?.Title ?? step.Step,
        GroupNode card => card.Name ?? "Run together",
        _ => fallback,
    };
}
