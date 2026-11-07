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
    /// <summary>A failure its fallback took over (D-065): dashed caution like a may-fail one.</summary>
    public bool IsHandled { get; init; }

    /// <summary>"Only if" wasn't met and the step was skipped (D-081): a hatched success bar.</summary>
    public bool IsConditionSkipped { get; init; }

    /// <summary>A "Run a flow" step that waited: its chevron shows the called flow's lanes (UX §5.5, D-062).</summary>
    public bool IsCall { get; init; }

    public bool IsExpanded { get; init; }

    /// <summary>A step of a called flow, shown under its call's bracket; its id is "call/step".</summary>
    public bool IsNested { get; init; }

    /// <summary>A may-fail step that failed, or a failure its fallback took over: dashed caution (UX §5.5).</summary>
    public bool IsMayFailFailed => Outcome == NodeOutcome.FailedIgnored || (IsHandled && Outcome == NodeOutcome.Failed);

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
    private readonly HashSet<string> _handled;

    /// <summary>Separates a call's id from its called flow's step ids in nested bars: a control character, so no node
    /// id can contain it.</summary>
    public const char Nested = '';

    private readonly Func<string, Flow?> _flowOf;
    private readonly HashSet<string> _expanded = new(StringComparer.Ordinal);
    private double _width = 600;

    /// <param name="flowOf">Finds a called flow by id, for its steps' names in nested lanes; null: ids only.</param>
    public RunDetailViewModel(RunRecord record, Flow? flow, Func<string, Flow?>? flowOf = null)
    {
        ArgumentNullException.ThrowIfNull(record);
        Record = record;
        Flow = flow;
        _flowOf = flowOf ?? (_ => null);
        _titles = record.Nodes.ToDictionary(n => n.NodeId, n => TitleOf(flow?.FindNode(n.NodeId), n.NodeId), StringComparer.Ordinal);
        Timeline = Build(record, flow);
        Sentence = RunWords.Sentence(record, NodeTitle, flow);
        // Only failures whose fallback started in this run (not merely ones the flow has a fallback for).
        _handled = flow is null ? new HashSet<string>()
            : Outcomes.Fixes(flow, record.Nodes).Select(f => f.Failed).ToHashSet(StringComparer.Ordinal);
        // Before 0.15 nothing recorded when steps started: every bar would sit at zero.
        HasTimeline = record.Nodes.Any(n => n.Order > 0);
        // A card has no attempts of its own even when it ran; its outcome says whether it was skipped.
        NotRun = [.. record.Nodes.Where(n => n.Attempts == 0
                && n.Outcome is NodeOutcome.Skipped or NodeOutcome.Cancelled or NodeOutcome.NotNeeded
                && flow?.FindNode(n.NodeId) is not GroupNode)
            .Select(n => $"{NodeTitle(n.NodeId)}: {NotRunWords(n.Outcome)}")];
        Layout(600);
    }

    public RunRecord Record { get; }

    /// <summary>The flow as it is now may list a card member twice (a problem the editor shows): draw no brackets then.</summary>
    private static RunTimeline Build(RunRecord record, Flow? flow)
    {
        try
        {
            return RunTimeline.Build(record, flow);
        }
        catch (ArgumentException)
        {
            return RunTimeline.Build(record, null);
        }
    }

    /// <summary>The flow as it is now; null when it was deleted (no brackets, "Open in editor" off).</summary>
    public Flow? Flow { get; }

    public RunTimeline Timeline { get; }

    public bool HasTimeline { get; }

    public bool CanOpenInEditor => SelectedCall is { } call ? _flowOf(call.FlowId) is not null : Flow is not null;

    /// <summary>"Open in editor": this run, or with a nested bar selected the called flow's run (UX §5.5).</summary>
    public RunRecord RecordToOpen => SelectedCall is { } call
        ? new RunRecord(call.RunId, call.FlowId, call.FlowName, Record.Trigger, Record.Started,
            NodeOf(SelectedBar!.NodeId.Split(Nested)[0])?.Duration ?? Record.Duration, call.Outcome, call.Nodes)
        : Record;

    /// <summary>The nested run the selected bar belongs to, or null for this run's own bars.</summary>
    private CalledRun? SelectedCall => SelectedBar is { IsNested: true } bar ? NodeOf(bar.NodeId.Split(Nested)[0])?.Called : null;

    /// <summary>Shows or hides the lanes of the flow a call ran.</summary>
    public void ToggleCall(string nodeId)
    {
        if (NodeOf(nodeId)?.Called is null)
            return;
        if (!_expanded.Remove(nodeId))
            _expanded.Add(nodeId);
        else if (SelectedBar is { IsNested: true } bar && bar.NodeId.StartsWith(nodeId + Nested, StringComparison.Ordinal))
            SelectedBar = null;
        Layout(_width);
    }

    /// <summary>A bar's result: this run's node, or "call/step" inside the call's nested run.</summary>
    private NodeResult? NodeOf(string id)
    {
        var parts = id.Split(Nested, 2);
        var node = Record.Nodes.FirstOrDefault(n => n.NodeId == parts[0]);
        return parts.Length == 1 ? node : node?.Called?.Nodes.FirstOrDefault(n => n.NodeId == parts[1]);
    }

    public string FlowName => Record.FlowName;

    /// <summary>"Connect Technics is done." (UX §5.7 #13, #14, #16).</summary>
    public string Sentence { get; }

    /// <summary>"Alt+Z · Today 14:02 · 3.1 s".</summary>
    public string Facts => Captions.Join(Record.Trigger, Record.Started.ToLocalTime().ToString("g", CultureInfo.CurrentCulture),
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
    public IReadOnlyList<string> SelectedLines => SelectedBar is { } bar && NodeOf(bar.NodeId) is { } node
        ? Lines(node, bar.Title)
        : [];

    partial void OnSelectedBarChanged(TimelineBarView? value) => OnPropertyChanged(nameof(CanOpenInEditor));

    /// <summary>One bar to place: this run's, or a called flow's moved to its call's time and below this run's lanes.</summary>
    private sealed record Item(string NodeId, string Title, int Lane, TimeSpan Start, TimeSpan End, NodeOutcome Outcome,
        IReadOnlyList<TimeSpan> Notches, bool Handled, bool IsCall, bool IsNested);

    /// <summary>Lays the timeline out <paramref name="width"/> DIP wide (the view calls it when its width changes).</summary>
    public void Layout(double width)
    {
        width = Math.Max(width, 100);
        _width = width;
        var items = Timeline.Bars.Select(b => new Item(b.NodeId, NodeTitle(b.NodeId), b.Lane, b.Start, b.End, b.Outcome, b.Notches,
            _handled.Contains(b.NodeId), NodeOf(b.NodeId)?.Called is not null, false)).ToList();
        var lanes = Math.Max(1, Timeline.Lanes);
        var calls = new List<(string Title, int FirstLane, int LastLane, string CallId)>();
        foreach (var call in items.Where(i => i.IsCall && _expanded.Contains(i.NodeId)).OrderBy(i => i.Start).ToList())
        {
            var called = NodeOf(call.NodeId)!.Called!;
            var flow = _flowOf(called.FlowId);
            var nested = Build(new RunRecord(called.RunId, called.FlowId, called.FlowName, "", Record.Started, call.End - call.Start,
                called.Outcome, called.Nodes), flow);
            // Called holds the last try: it ended when the call did.
            var offset = call.End - nested.Length > call.Start ? call.End - nested.Length : call.Start;
            items.AddRange(nested.Bars.Select(b => new Item(call.NodeId + Nested + b.NodeId, TitleOf(flow?.FindNode(b.NodeId), b.NodeId),
                lanes + b.Lane, offset + b.Start, offset + b.End, b.Outcome, [.. b.Notches.Select(n => offset + n)], false, false, true)));
            calls.Add((called.FlowName, lanes, lanes + Math.Max(1, nested.Lanes) - 1, call.NodeId));
            lanes += Math.Max(1, nested.Lanes);
        }
        var length = Math.Max(Math.Max(Timeline.Length.TotalSeconds, items.Count == 0 ? 0 : items.Max(i => i.End.TotalSeconds)), 0.001);
        var scale = width / length; // DIP per second
        double X(TimeSpan t) => Math.Min(t.TotalSeconds * scale, width);
        var top = AxisHeight;
        // Bars widened to MinBarWidth would cover each other: in each lane, a bar starts BarGap after the previous
        // one ends, then the lane is fitted back from the right edge so the last bars keep their gap too.
        var placed = items.OrderBy(b => b.Start).Select(b => (Bar: b, X: X(b.Start), W: Math.Max(X(b.End) - X(b.Start), MinBarWidth))).ToArray();
        foreach (var lane in placed.Select((p, i) => (p.Bar.Lane, i)).GroupBy(p => p.Lane, p => p.i))
        {
            var end = double.NegativeInfinity;
            foreach (var i in lane)
            {
                placed[i].X = Math.Max(placed[i].X, end + BarGap);
                end = placed[i].X + placed[i].W;
            }
            var limit = width;
            foreach (var i in lane.Reverse())
            {
                // Trim a long bar before moving it, so start times stay where they were.
                if (placed[i].X + placed[i].W > limit)
                    placed[i].W = Math.Max(MinBarWidth, limit - placed[i].X);
                placed[i].X = Math.Max(0, Math.Min(placed[i].X, limit - placed[i].W));
                limit = placed[i].X - BarGap;
            }
        }
        var bars = placed.Select(p => new TimelineBarView(p.Bar.NodeId, p.Bar.Title, p.Bar.Outcome, p.X,
            top + p.Bar.Lane * LaneHeight + (LaneHeight - BarHeight) / 2, p.W, [.. p.Bar.Notches.Select(n => Math.Clamp(X(n) - p.X, 0, p.W))])
        {
            IsHandled = p.Bar.Handled,
            IsConditionSkipped = NodeOf(p.Bar.NodeId)?.SkippedBecause is { Length: > 0 },
            IsCall = p.Bar.IsCall,
            IsExpanded = p.Bar.IsCall && _expanded.Contains(p.Bar.NodeId),
            IsNested = p.Bar.IsNested,
            AutomationName = (NodeOf(p.Bar.NodeId)?.SkippedBecause is { Length: > 0 } skip
                ? $"{p.Bar.Title}, skipped: {skip}"
                : $"{p.Bar.Title}, {Describe(p.Bar.Outcome)}, {RunWords.Seconds(p.Bar.End - p.Bar.Start)}")
                + (p.Bar.IsCall ? _expanded.Contains(p.Bar.NodeId) ? ", its steps shown" : ", has steps of its own" : ""),
        }).ToList();
        // A bracket hugs its members' bars as fitted above, not their raw times, so it never cuts one off.
        Brackets = [.. Timeline.Brackets.Select(c =>
        {
            var members = (Flow?.FindNode(c.CardId) as GroupNode)?.Members.ToHashSet(StringComparer.Ordinal);
            var inside = bars.Where(b => members?.Contains(b.NodeId) == true).ToList();
            var left = inside.Count > 0 ? inside.Min(b => b.X) : X(c.Start);
            var right = inside.Count > 0 ? inside.Max(b => b.X + b.Width) : X(c.End);
            return new TimelineBracketView(TitleOf(Flow?.FindNode(c.CardId), "Run together"), left, top + c.FirstLane * LaneHeight + 2,
                Math.Max(right - left, MinBarWidth), (c.LastLane - c.FirstLane + 1) * LaneHeight - 4);
        }), .. calls.Select(c =>
        {
            // The called flow's lanes, under a bracket named after it (like a card's).
            var inside = bars.Where(b => b.NodeId.StartsWith(c.CallId + Nested, StringComparison.Ordinal)).ToList();
            var left = inside.Count > 0 ? inside.Min(b => b.X) : 0;
            var right = inside.Count > 0 ? inside.Max(b => b.X + b.Width) : left + MinBarWidth;
            return new TimelineBracketView(c.Title, left, top + c.FirstLane * LaneHeight + 2, Math.Max(right - left, MinBarWidth),
                (c.LastLane - c.FirstLane + 1) * LaneHeight - 4);
        })];
        Ticks = [.. TickTimes(Timeline.Length).Select(t => new TimelineTick(X(t), RunWords.Seconds(t)))];
        Height = top + lanes * LaneHeight;
        Bars = bars; // last: the view redraws on Bars, so everything else is in place by then
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

    private static List<string> Lines(NodeResult node, string title)
    {
        // "Skipped: Technics was already connected" (D-081); a skip is still a Succeeded record.
        var lines = new List<string> { node.SkippedBecause is { Length: > 0 } why ? $"Skipped: {why}" : $"{title}: {Describe(node.Outcome)}" };
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
        NodeOutcome.NotNeeded => "not needed",
        _ => "stopped",
    };

    private static string NotRunWords(NodeOutcome outcome) => outcome switch
    {
        NodeOutcome.Cancelled => "stopped before it started",
        NodeOutcome.NotNeeded => "not needed", // #47
        _ => "skipped",
    };

    private string NodeTitle(string id) => _titles.GetValueOrDefault(id, id);

    private static string TitleOf(Node? node, string fallback) => node switch
    {
        StepNode step => step.Name ?? ProviderCatalog.Find(step.Step)?.Title ?? step.Step,
        GroupNode card => card.Name ?? "Run together",
        _ => fallback,
    };
}
