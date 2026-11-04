using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using DazeControl.Core.Layout;
using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Editor.ViewModels.Config;

namespace DazeControl.Editor.ViewModels.Canvas;

public enum FocusMove
{
    Left,
    Right,
    Up,
    Down,
}

/// <summary>
/// One flow on the canvas (UX §5.2): pills, cards and edges laid out by <see cref="FlowLayout"/> (D-020), the
/// selection and the keyboard focus. The WinUI control only draws this and forwards gestures; every edit goes
/// through <see cref="Edit"/>.
/// </summary>
public sealed partial class FlowCanvasViewModel : ObservableObject, IDisposable
{
    public const double MinZoom = 0.5, MaxZoom = 2.0;

    /// <summary>Below this, pills show only their title (UX §5.8 #1, semantic zoom).</summary>
    public const double DetailZoom = 0.6;

    public static readonly Size DefaultPillSize = new(232, 56);

    private readonly ConfigSession _session;
    private readonly Dictionary<string, Size> _measured = new(StringComparer.Ordinal);
    private readonly HashSet<string> _selected = new(StringComparer.Ordinal);
    private readonly HashSet<(string From, string To)> _selectedEdges = [];
    private readonly CanvasClipboard _clipboard;
    private readonly List<Flow> _undo = [], _redo = [];
    private FlowArrangement _arrangement = new([], [], [], new Size(96, 96));
    private Flow? _known;
    private bool _editing;

    /// <summary>Undo steps kept per open flow.</summary>
    public const int HistoryLimit = 100;

    public FlowCanvasViewModel(ConfigSession session, string flowId, CanvasClipboard? clipboard = null)
    {
        _session = session;
        _clipboard = clipboard ?? new CanvasClipboard();
        FlowId = flowId;
        _session.PropertyChanged += OnSessionChanged;
        _session.FlowRenamed += OnFlowRenamed;
        _known = Flow;
        Sync();
    }

    /// <summary>Changes when a saved rename gives the flow a new id (D-075): the canvas stays open.</summary>
    public string FlowId { get; private set; }

    private void OnFlowRenamed(string oldId, string newId)
    {
        if (oldId != FlowId)
            return;
        FlowId = newId;
        OnPropertyChanged(nameof(FlowId));
    }

    /// <summary>Top-level pills and cards first, then card members, so members draw above their card.</summary>
    public ObservableCollection<CanvasNodeViewModel> Nodes { get; } = [];

    public ObservableCollection<CanvasEdgeViewModel> Edges { get; } = [];

    [ObservableProperty]
    public partial string FlowName { get; private set; } = "";

    /// <summary>The flow was deleted (or renamed away) under the editor.</summary>
    [ObservableProperty]
    public partial bool IsMissing { get; private set; }

    [ObservableProperty]
    public partial bool IsEmpty { get; private set; }

    [ObservableProperty]
    public partial double ExtentWidth { get; private set; }

    [ObservableProperty]
    public partial double ExtentHeight { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowDetails), nameof(ZoomText))]
    public partial double Zoom { get; set; } = 1;

    public bool ShowDetails => Zoom >= DetailZoom;

    public string ZoomText => $"{Math.Round(Zoom * 100)} %";

    [ObservableProperty]
    public partial string? FocusedId { get; private set; }

    /// <summary>False while the file can't be written, and while a run is on the canvas (UX §5.5, §5.7 #22).</summary>
    public bool CanEdit => _session.CanEdit && !IsRunLocked;

    /// <summary>A live run of this flow is on the canvas: editing waits until it finishes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    public partial bool IsRunLocked { get; internal set; }

    private IReadOnlyDictionary<string, NodeRunView>? _run;

    /// <summary>Shows a run's states on the pills and edges; null goes back to editing.</summary>
    public void ShowRun(IReadOnlyDictionary<string, NodeRunView>? states)
    {
        _run = states;
        ApplyRun();
    }

    public bool IsShowingRun => _run is not null;

    private void ApplyRun()
    {
        foreach (var node in Nodes)
            node.ShowRun(_run?.GetValueOrDefault(node.Id));
        static bool Finished(NodeRunView? view) =>
            view?.State is NodeRunState.Succeeded or NodeRunState.Failed or NodeRunState.FailedMayFail;
        // A card has no state of its own: it has finished once every member has.
        bool SourceFinished(string id) => Flow?.FindNode(id) is GroupNode card
            ? card.Members.Count > 0 && card.Members.All(m => Finished(_run?.GetValueOrDefault(m)))
            : Finished(_run?.GetValueOrDefault(id));
        foreach (var edge in Edges)
            edge.IsLit = _run is not null && SourceFinished(edge.From);
        OnPropertyChanged(nameof(IsShowingRun));
    }

    [ObservableProperty]
    public partial bool CanUndo { get; private set; }

    /// <summary>Edits not saved yet (D-047): Save writes them, Cancel throws them away.</summary>
    public bool HasChanges => _session.HasDraft(FlowId);

    /// <summary>Save (Ctrl+S): the draft goes to config.yaml, and the engine picks it up.</summary>
    /// <summary>False when the write failed: the draft and its undo history stay for another try.</summary>
    public bool Save()
    {
        if (!HasChanges)
            return true;
        if (!_session.SaveDraft(FlowId))
            return false;
        ClearHistory(); // undo stays inside a draft
        return true;
    }

    /// <summary>Cancel / Discard: back to the saved flow.</summary>
    public void Discard()
    {
        if (!HasChanges)
            return;
        _session.DiscardDraft(FlowId);
        ClearHistory();
    }

    private void ClearHistory()
    {
        _undo.Clear();
        _redo.Clear();
        UpdateHistory();
    }

    [ObservableProperty]
    public partial bool CanRedo { get; private set; }

    /// <summary>The Outline view's rows (UX §5.2): run order, cards as branches.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<OutlineItem> Outline { get; private set; } = [];

    /// <summary>This flow's problems, errors first, in canvas order (UX §5.1 "Problems (n)").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProblemsLabel), nameof(HasProblems))]
    public partial IReadOnlyList<ProblemItem> Problems { get; private set; } = [];

    public bool HasProblems => Problems.Count > 0;

    public string ProblemsLabel => $"Problems ({Problems.Count})";

    /// <summary>A row of the Problems list picked: select its step (the inspector follows), or the flow's settings.</summary>
    public void Show(ProblemItem problem)
    {
        ArgumentNullException.ThrowIfNull(problem);
        if (problem.NodeId is { } id && Find(id) is not null)
            Select(id);
        else
            ClearSelection();
    }

    public bool CanPaste => _clipboard.Clip is not null;

    public IReadOnlyCollection<string> SelectedIds => _selected;

    public FlowArrangement Arrangement => _arrangement;

    public Flow? Flow => _session.Current.Flows.FirstOrDefault(f => f.Id == FlowId);

    /// <summary>The flow as last saved: what Run executes (D-047); the draft is <see cref="Flow"/>.</summary>
    public Flow? SavedFlow => _session.SavedFlow(FlowId);

    public Library Library => _session.Current.Library;

    public CanvasNodeViewModel? Find(string id) => Nodes.FirstOrDefault(n => n.Id == id);

    /// <summary>The control measured a pill; a changed size re-lays out (text scaling, UX §5.8 #5).</summary>
    public void ReportSize(string id, double width, double height)
    {
        var size = new Size(Math.Max(width, DefaultPillSize.Width), Math.Max(height, DefaultPillSize.Height));
        if (_measured.TryGetValue(id, out var known) && Math.Abs(known.Width - size.Width) < 0.5 && Math.Abs(known.Height - size.Height) < 0.5)
            return;
        _measured[id] = size;
        Sync();
    }

    /// <summary>
    /// The view that shows the whole flow, never above 100 %, centred in the pan room (D-074); still too big at the
    /// smallest zoom, it shows where the flow starts.
    /// </summary>
    public CanvasView FitView(double viewportWidth, double viewportHeight)
    {
        if (viewportWidth <= 0 || viewportHeight <= 0 || ExtentWidth <= 0 || ExtentHeight <= 0)
            return new CanvasView(0, 0, 1);
        var zoom = Math.Clamp(Math.Min(viewportWidth / ExtentWidth, viewportHeight / ExtentHeight), MinZoom, 1);
        return CanvasViewport.Fit(zoom, ExtentWidth, ExtentHeight, viewportWidth, viewportHeight);
    }

    // ---- Selection (UX §5.2) ----

    public void Select(string id)
    {
        _selected.Clear();
        _selectedEdges.Clear();
        if (Find(id) is not null)
            _selected.Add(id);
        FocusedId = Find(id)?.Id ?? FocusedId;
        ApplySelection();
    }

    /// <summary>Ctrl+click.</summary>
    public void Toggle(string id)
    {
        if (Find(id) is null)
            return;
        if (!_selected.Remove(id))
            _selected.Add(id);
        FocusedId = id;
        ApplySelection();
    }

    /// <summary>
    /// Marquee: every node touching <paramref name="area"/> (content DIP) and every edge with both ends inside it;
    /// additive with Ctrl.
    /// </summary>
    public void SelectIn(Rect area, bool additive)
    {
        if (!additive)
        {
            _selected.Clear();
            _selectedEdges.Clear();
        }
        foreach (var node in Nodes.Where(n => n.Bounds.Intersects(area)))
            _selected.Add(node.Id);
        foreach (var edge in Edges.Where(e => area.Contains(e.Curve.Start) && area.Contains(e.Curve.Tip)))
            _selectedEdges.Add((edge.From, edge.To));
        ApplySelection();
    }

    /// <summary>A click on an edge (UX §5.8 #2): selects it alone; Ctrl adds or removes it.</summary>
    public void SelectEdge(CanvasEdgeViewModel edge, bool additive = false)
    {
        ArgumentNullException.ThrowIfNull(edge);
        if (!additive)
        {
            _selected.Clear();
            _selectedEdges.Clear();
        }
        if (!_selectedEdges.Remove((edge.From, edge.To)))
            _selectedEdges.Add((edge.From, edge.To));
        ApplySelection();
    }

    /// <summary>Edges picked themselves (a click or the marquee), not just lit because both ends are selected.</summary>
    public IReadOnlyList<CanvasEdgeViewModel> SelectedEdges => [.. Edges.Where(e => _selectedEdges.Contains((e.From, e.To)))];

    /// <summary>The edge under the pointer, drawn thicker in accent with its "+"/"×" controls (UX §5.3); null for none.</summary>
    public CanvasEdgeViewModel? HoveredEdge
    {
        get => Edges.FirstOrDefault(e => e.IsHovered);
        set
        {
            foreach (var edge in Edges)
                edge.IsHovered = ReferenceEquals(edge, value);
        }
    }

    public void SelectAll()
    {
        foreach (var node in Nodes)
            _selected.Add(node.Id);
        ApplySelection();
    }

    public void ClearSelection()
    {
        _selected.Clear();
        _selectedEdges.Clear();
        ApplySelection();
    }

    /// <summary>The topmost node under a content point: a member before its card.</summary>
    public CanvasNodeViewModel? HitTest(Point p) =>
        Nodes.Where(n => n.Bounds.Contains(p)).OrderBy(n => n.Kind == CanvasNodeKind.Card ? 1 : 0).FirstOrDefault();

    /// <summary>The nearest edge within <paramref name="band"/> DIP (12 mouse, 20 touch; UX §5.8 #2).</summary>
    public CanvasEdgeViewModel? HitTestEdge(Point p, double band = 12) =>
        Edges.Select(e => (Edge: e, Distance: e.Curve.DistanceTo(p))).Where(x => x.Distance <= band)
            .OrderBy(x => x.Distance).Select(x => x.Edge).FirstOrDefault();

    // ---- Keyboard: one tab stop, roving focus (UX §5.2) ----

    /// <summary>The canvas got focus: keep the last focused node, else the first one in run order.</summary>
    public void EnsureFocus()
    {
        if (FocusedId is null || Find(FocusedId) is null)
            SetFocus(Nodes.OrderBy(n => n.Column).ThenBy(n => n.Y).FirstOrDefault()?.Id);
    }

    /// <summary>Right/Left: the nearest node in the next/previous column; Up/Down: the same column.</summary>
    public void MoveFocus(FocusMove move)
    {
        EnsureFocus();
        if (FocusedId is null || Find(FocusedId) is not { } from)
            return;
        CanvasNodeViewModel? to = move switch
        {
            FocusMove.Up => Nodes.Where(n => n.Column == from.Column && n.Y < from.Y - 0.5).OrderByDescending(n => n.Y).FirstOrDefault(),
            FocusMove.Down => Nodes.Where(n => n.Column == from.Column && n.Y > from.Y + 0.5).OrderBy(n => n.Y).FirstOrDefault(),
            // Across columns land on a step or card, not inside a card; Up/Down then walks its members.
            _ => Nodes.Where(n => n.Column == from.Column + (move == FocusMove.Right ? 1 : -1) && n.Kind != CanvasNodeKind.Member)
                .OrderBy(n => Math.Abs(n.Bounds.CenterY - from.Bounds.CenterY)).FirstOrDefault(),
        };
        if (to is not null)
        {
            SetFocus(to.Id);
            Select(to.Id);
        }
    }

    private void SetFocus(string? id)
    {
        FocusedId = id;
        foreach (var node in Nodes)
            node.IsFocused = node.Id == id;
    }

    // ---- Reorder within a column (D-020: order in a column = order in `nodes`) ----

    /// <summary>Alt+Up/Down.</summary>
    public void MoveInColumn(string id, int delta)
    {
        if (Find(id) is { } node)
            MoveToRow(id, node.Row + delta);
    }

    /// <summary>The row a dragged node would take if dropped at content y <paramref name="y"/> in its own column.</summary>
    public int RowAt(string id, double y)
    {
        if (Find(id) is not { } node)
            return 0;
        return Siblings(node).Count(n => n.Id != id && n.Bounds.CenterY < y);
    }

    /// <summary>Puts a node at <paramref name="row"/> among its column (or card) siblings.</summary>
    public void MoveToRow(string id, int row)
    {
        if (Find(id) is not { } node)
            return;
        var order = Siblings(node).Select(n => n.Id).ToList();
        row = Math.Clamp(row, 0, order.Count - 1);
        if (order.IndexOf(id) == row)
            return;
        order.Remove(id);
        order.Insert(row, id);
        if (node.CardId is { } cardId)
        {
            Edit(flow => flow with
            {
                Nodes = [.. flow.Nodes.Select(n => n is GroupNode g && g.Id == cardId
                    ? g with { Members = [.. order, .. g.Members.Where(m => !order.Contains(m))] }
                    : n)],
            });
            return;
        }
        // Rewrite `nodes` so this column's ids appear in the new order, each in one of the slots the column used.
        Edit(flow =>
        {
            var inColumn = order.ToHashSet(StringComparer.Ordinal);
            var queue = new Queue<string>(order);
            var byId = flow.Nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            return flow with
            {
                Nodes = [.. flow.Nodes.Select(n => inColumn.Contains(n.Id) && seen.Add(n.Id) ? byId[queue.Dequeue()] : n)],
            };
        });
    }

    private IEnumerable<CanvasNodeViewModel> Siblings(CanvasNodeViewModel node) =>
        Nodes.Where(n => n.Column == node.Column && n.CardId == node.CardId).OrderBy(n => n.Row);

    // ---- Connect (UX §5.2): drag from an output port (right) to a node's input port or body ----

    /// <summary>Port hit radius in DIP: 24-DIP target (UX §5.3); touch passes 20 for the 40-DIP one.</summary>
    public const double PortRadius = 12;

    public static Point OutPort(CanvasNodeViewModel node) => new(node.X + node.Width, node.Y + node.Height / 2);

    public static Point InPort(CanvasNodeViewModel node) => new(node.X, node.Y + node.Height / 2);

    /// <summary>The step or card whose output port is under <paramref name="p"/>; members have none (ports on the card).</summary>
    public CanvasNodeViewModel? OutPortAt(Point p, double radius = PortRadius) =>
        Nodes.Where(n => n.Kind != CanvasNodeKind.Member)
            .Select(n => (Node: n, Distance: Distance(OutPort(n), p)))
            .Where(x => x.Distance <= radius).OrderBy(x => x.Distance).Select(x => x.Node).FirstOrDefault();

    /// <summary>
    /// Where a connect drag released at <paramref name="p"/> ends: the node under it (a member counts as its card), else
    /// the node whose input port is within <paramref name="radius"/> (the dot overhangs the node's left edge).
    /// </summary>
    public CanvasNodeViewModel? ConnectTargetAt(Point p, double radius = PortRadius)
    {
        var node = HitTest(p) ?? Nodes.Where(n => n.Kind != CanvasNodeKind.Member)
            .Select(n => (Node: n, Distance: Distance(InPort(n), p)))
            .Where(x => x.Distance <= radius).OrderBy(x => x.Distance).Select(x => x.Node).FirstOrDefault();
        return node?.CardId is { } card ? Find(card) : node;
    }

    public ConnectRefusal CanConnect(string from, string to) =>
        Flow is { } flow ? FlowGraph.CanConnect(flow, from, to) : ConnectRefusal.Unknown;

    /// <summary>Makes <paramref name="to"/> wait for <paramref name="from"/>; a refusal changes nothing.</summary>
    public ConnectRefusal Connect(string from, string to)
    {
        var refusal = CanConnect(from, to);
        if (refusal == ConnectRefusal.None)
            Edit(flow => FlowGraph.Connect(flow, from, to));
        return refusal;
    }

    /// <summary>What the canvas says when it refuses (UX §5.7 #3, #4); null = say nothing.</summary>
    public static string? Explain(ConnectRefusal refusal) => refusal switch
    {
        ConnectRefusal.Loop => "That would make a loop. A step can't wait for something that waits for it.",
        ConnectRefusal.SameCard => "Steps in a card start together, so they can't wait for each other.",
        ConnectRefusal.Conditions => "These steps wait for the same step under different conditions, so they can't share a card.",
        ConnectRefusal.FailureFallback => "A step here runs only if something fails, and this change would alter which failures count. Change or remove that \"if it fails\" connection first.",
        ConnectRefusal.FailurePolicies => "These steps handle failure differently, so they can't share a card. Give them the same failure setting first.",
        _ => null,
    };

    public void Disconnect(CanvasEdgeViewModel edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        Edit(flow => FlowGraph.Disconnect(flow, edge.From, edge.To));
    }

    /// <summary>
    /// Edge menu and Connections: "Only if this fails" (Failure), "Whatever happens" (Always), back to Success. A
    /// fallback switches its source off "Stop the flow" (<see cref="WithFallbackPolicy"/>).
    /// </summary>
    public void SetWhen(CanvasEdgeViewModel edge, EdgeWhen when)
    {
        ArgumentNullException.ThrowIfNull(edge);
        EditFallback(edge.From, flow => FlowGraph.SetWhen(flow, edge.From, edge.To, when));
    }

    /// <summary>A canvas note (not a refusal) to show beside <see cref="CanvasNote.At"/>: the edit is done.</summary>
    public event EventHandler<CanvasNote>? Noted;

    private void Note(string text, string at) => Noted?.Invoke(this, new CanvasNote(text, at));

    public const string FallbackPolicyNote =
        "If this fails, its fallback runs and the flow carries on, so it's set to \"Skip what depends on it\" now.";

    /// <summary>
    /// A step or card with an "if it fails" link and "Stop the flow" runs the fallback and goes on (D-024), which is
    /// what "Skip what depends on it" says, so the policy shows that (D-059). Skip and Keep going stay as chosen.
    /// </summary>
    public static Flow WithFallbackPolicy(Flow flow, string source)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var top = FlowGraph.TopLevel(flow, source);
        var cardOf = FlowLayout.CardOf(flow);
        if (flow.FindNode(top) is not { OnFailure: FailurePolicy.Stop }
            || !FlowLayout.Edges(flow, cardOf).Any(e => e.From == top && e.When == EdgeWhen.Failure))
            return flow;
        return flow with { Nodes = [.. flow.Nodes.Select(n => n.Id == top ? n with { OnFailure = FailurePolicy.Skip } : n)] };
    }

    /// <summary>One undo step: <paramref name="change"/> plus the fallback policy of <paramref name="source"/>; noted.</summary>
    private void EditFallback(string source, Func<Flow, Flow> change)
    {
        var switched = false;
        Edit(flow =>
        {
            var changed = change(flow);
            var next = WithFallbackPolicy(changed, source);
            switched = !ReferenceEquals(next, changed);
            return next;
        });
        if (switched && Flow is { } after)
            Note(FallbackPolicyNote, FlowGraph.TopLevel(after, source));
    }

    // ---- "If it fails, run…" (UX §5.4): fallbacks without hunting for an edge's menu ----

    /// <summary>A new step that runs only if <paramref name="from"/> (its card for a member) fails; selected. One undo step.</summary>
    public string? AddFallback(string from, string step)
    {
        if (Flow is null || !CanEdit || Flow.FindNode(from) is null)
            return null;
        string? id = null;
        EditFallback(from, flow =>
        {
            (var next, id) = FlowCommands.Add(flow, step, [from]);
            return next with
            {
                Nodes = [.. next.Nodes.Select(n => n.Id == id ? n with { After = [.. n.After.Select(e => e with { When = EdgeWhen.Failure })] } : n)],
            };
        });
        SelectOnly(id is null ? [] : [id]);
        return id;
    }

    /// <summary>Makes <paramref name="to"/> run only if <paramref name="from"/> fails: a new edge, or the existing one changed.</summary>
    public ConnectRefusal LinkFallback(string from, string to)
    {
        if (Flow is not { } flow)
            return ConnectRefusal.Unknown;
        var refusal = FlowGraph.CanConnect(flow, from, to);
        if (refusal == ConnectRefusal.Exists)
            EditFallback(from, f => FlowGraph.SetWhen(f, FlowGraph.TopLevel(f, from), FlowGraph.TopLevel(f, to), EdgeWhen.Failure));
        else if (refusal == ConnectRefusal.None)
            EditFallback(from, f => FlowGraph.Connect(f, from, to, EdgeWhen.Failure));
        return refusal is ConnectRefusal.Exists ? ConnectRefusal.None : refusal;
    }

    /// <summary>
    /// Steps and cards that could run only if <paramref name="from"/> fails: not itself, not its own card, nothing it
    /// waits for (a loop), and not one that already does.
    /// </summary>
    public IReadOnlyList<CanvasNodeViewModel> FallbackCandidates(string from)
    {
        if (Flow is not { } flow || flow.FindNode(from) is null)
            return [];
        var source = FlowGraph.TopLevel(flow, from);
        var failing = Edges.Where(e => e.From == source && e.When == EdgeWhen.Failure).Select(e => e.To).ToHashSet(StringComparer.Ordinal);
        return [.. Nodes.Where(n => n.Kind != CanvasNodeKind.Member && !failing.Contains(n.Id)
                && FlowGraph.CanConnect(flow, from, n.Id) is ConnectRefusal.None or ConnectRefusal.Exists)
            .OrderBy(n => n.Column).ThenBy(n => n.Row)];
    }

    // ---- Cards (UX §5.2): Ctrl+G, Ctrl+Shift+G, drop zones ----

    public const string DroppedFallbackNote =
        "These steps start together now, so the \"if it fails\" or \"whatever happens\" connection between them is gone.";

    /// <summary>Ctrl+G / "Run together" on the selection (steps and cards, in `nodes` order); selects the card.</summary>
    public ConnectRefusal GroupSelection()
    {
        if (Flow is not { } flow)
            return ConnectRefusal.Unknown;
        var ids = flow.Nodes.Select(n => n.Id).Where(_selected.Contains).ToList();
        var (next, refusal, droppedFallback) = FlowEdits.Group(flow, ids);
        if (refusal != ConnectRefusal.None || next == flow)
            return refusal;
        // Only ids Group accepted (cards and loose steps); a member of another card was ignored.
        var before = FlowLayout.CardOf(flow);
        var cardOf = FlowLayout.CardOf(next);
        var card = ids.Where(id => !before.ContainsKey(id))
            .Select(id => next.FindNode(id) is GroupNode ? id : cardOf.GetValueOrDefault(id)).First(id => id is not null)!;
        Edit(_ => next);
        Select(card);
        if (droppedFallback)
            Note(DroppedFallbackNote, card);
        return ConnectRefusal.None;
    }

    /// <summary>Ctrl+Shift+G: every selected card; its members end up selected.</summary>
    /// <summary>Ctrl+Shift+G on the selected cards; all or none (the first refusal wins).</summary>
    public ConnectRefusal UngroupSelection()
    {
        if (Flow is not { } flow)
            return ConnectRefusal.None;
        var cards = flow.Nodes.OfType<GroupNode>().Where(g => _selected.Contains(g.Id)).ToList();
        if (cards.Count == 0)
            return ConnectRefusal.None;
        var next = flow;
        foreach (var card in cards)
        {
            (next, var refusal) = FlowEdits.Ungroup(next, card.Id);
            if (refusal != ConnectRefusal.None)
                return refusal;
        }
        Edit(_ => next);
        _selected.Clear();
        foreach (var member in cards.SelectMany(c => c.Members).Where(id => Find(id) is not null))
            _selected.Add(member);
        ApplySelection();
        return ConnectRefusal.None;
    }

    /// <summary>Where a dragged pill would land over <paramref name="target"/> at content point <paramref name="p"/>.</summary>
    public static DropZone ZoneAt(CanvasNodeViewModel target, Point p)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (p.X > target.X + target.Width * 2 / 3)
            return DropZone.RunAfter;
        // A card's body is all "run together"; a pill keeps thin top/bottom bands for reordering.
        var band = target.Kind == CanvasNodeKind.Card ? 0 : target.Height / 4;
        return p.Y >= target.Y + band && p.Y <= target.Y + target.Height - band ? DropZone.RunTogether : DropZone.None;
    }

    /// <summary>Drop <paramref name="dragged"/> on <paramref name="target"/>'s zone; a refusal changes nothing.</summary>
    public ConnectRefusal Drop(string dragged, string target, DropZone zone)
    {
        if (Flow is not { } flow || zone == DropZone.None)
            return ConnectRefusal.None;
        var (next, refusal, droppedFallback) = zone == DropZone.RunAfter
            ? new GroupResult(FlowEdits.RunAfter(flow, dragged, target).Flow, FlowEdits.RunAfter(flow, dragged, target).Refusal)
            : FlowEdits.RunTogether(flow, dragged, target);
        if (refusal == ConnectRefusal.None && next != flow)
        {
            Edit(_ => next);
            if (droppedFallback)
                Note(DroppedFallbackNote, FlowGraph.TopLevel(next, dragged));
        }
        return refusal;
    }

    /// <summary>A member dropped outside its card leaves it.</summary>
    public ConnectRefusal TakeOutOfCard(string member)
    {
        if (Flow is not { } flow)
            return ConnectRefusal.Unknown;
        var (next, refusal) = FlowEdits.TakeOut(flow, member);
        if (refusal == ConnectRefusal.None && next != flow)
            Edit(_ => next);
        return refusal;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    // ---- Edits ----

    /// <summary>"Tidy up" (UX §5.2): reorders columns to cut crossings; false when nothing changed.</summary>
    public bool TidyUp()
    {
        if (Flow is not { } flow || FlowTidy.Tidy(flow) is var tidy && ReferenceEquals(tidy, flow))
            return false;
        Edit(_ => tidy);
        return true;
    }

    /// <summary>Applies a change to this flow: one undo entry per call, so a gesture makes exactly one call.</summary>
    public void Edit(Func<Flow, Flow> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        if (Flow is not { } before || IsRunLocked)
            return;
        Replace(change);
        if (Flow is not { } after || after.Equals(before))
            return;
        Push(_undo, before);
        _redo.Clear();
        UpdateHistory();
    }

    /// <summary>Ctrl+Z.</summary>
    public void Undo() => Step(_undo, _redo);

    /// <summary>Ctrl+Y / Ctrl+Shift+Z.</summary>
    public void Redo() => Step(_redo, _undo);

    private void Step(List<Flow> from, List<Flow> to)
    {
        if (from.Count == 0 || Flow is not { } current || !CanEdit)
            return;
        var target = from[^1];
        from.RemoveAt(from.Count - 1);
        Replace(_ => target);
        Push(to, current);
        UpdateHistory();
    }

    private void Replace(Func<Flow, Flow> change)
    {
        _editing = true;
        try
        {
            _session.UpdateDraft(FlowId, change);
        }
        finally
        {
            _editing = false;
        }
        _known = Flow;
    }

    private static void Push(List<Flow> stack, Flow flow)
    {
        stack.Add(flow);
        if (stack.Count > HistoryLimit)
            stack.RemoveAt(0);
    }

    private void UpdateHistory()
    {
        CanUndo = _undo.Count > 0;
        CanRedo = _redo.Count > 0;
    }

    // ---- Add, delete, copy, paste (UX §5.2) ----

    /// <summary>Palette, quick-add or "Add a step after": a new step after <paramref name="after"/> (none = a first step); selected.</summary>
    public string? AddStep(string step, IEnumerable<string>? after = null)
    {
        if (Flow is null || !CanEdit)
            return null;
        string? id = null;
        Edit(flow =>
        {
            (var next, id) = FlowCommands.Add(flow, step, after ?? []);
            return next;
        });
        SelectOnly(id is null ? [] : [id]);
        return id;
    }

    /// <summary>"Add a step here" on an edge.</summary>
    public string? InsertStep(string step, CanvasEdgeViewModel edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        if (Flow is null || !CanEdit)
            return null;
        string? id = null;
        Edit(flow =>
        {
            (var next, id) = FlowCommands.Insert(flow, step, edge.From, edge.To);
            return next;
        });
        SelectOnly(id is null ? [] : [id]);
        return id;
    }

    /// <summary>Del: the selected steps, reconnecting around them, and the selected edges. One undo step.</summary>
    public void DeleteSelection()
    {
        if (_selected.Count == 0 && _selectedEdges.Count == 0)
            return;
        var ids = _selected.ToList();
        var edges = _selectedEdges.ToList();
        _selectedEdges.Clear();
        Edit(flow =>
        {
            // An edge at a deleted step (or a card emptied by it) goes with it; removing it first would change how the
            // gap reconnects.
            var gone = FlowCommands.DeletedBy(flow, ids);
            foreach (var (from, to) in edges.Where(e => !gone.Contains(e.From) && !gone.Contains(e.To)))
                flow = FlowGraph.Disconnect(flow, from, to);
            return ids.Count > 0 ? FlowCommands.Delete(flow, ids) : flow;
        });
    }

    /// <summary>Ctrl+C; false when nothing is selected.</summary>
    public bool Copy()
    {
        if (Flow is not { } flow || _selected.Count == 0)
            return false;
        _clipboard.Clip = FlowCommands.Copy(flow, _selected);
        OnPropertyChanged(nameof(CanPaste));
        return true;
    }

    /// <summary>Ctrl+V: the copied steps run after the selection (nothing selected: beside where they came from); selected.</summary>
    public void Paste()
    {
        if (_clipboard.Clip is { } clip)
            PasteClip(clip, _selected.Count > 0 ? [.. _selected] : null);
    }

    /// <summary>Ctrl+D: copies of the selection beside it; the clipboard keeps what it had.</summary>
    public void Duplicate()
    {
        if (Flow is { } flow && _selected.Count > 0)
            PasteClip(FlowCommands.Copy(flow, _selected), after: null);
    }

    private void PasteClip(FlowClip clip, List<string>? after)
    {
        if (!CanEdit)
            return;
        ValueList<string> ids = [];
        Edit(flow =>
        {
            (var next, ids) = FlowCommands.Paste(flow, clip, after);
            return next;
        });
        SelectOnly(ids);
    }

    /// <summary>Replaces the selection (the Outline view's multi-select); focus goes to the first.</summary>
    public void SelectOnly(IEnumerable<string> ids)
    {
        _selected.Clear();
        _selectedEdges.Clear();
        foreach (var id in ids.Where(id => Find(id) is not null))
            _selected.Add(id);
        if (_selected.Count > 0)
            SetFocus(_selected.First());
        ApplySelection();
    }

    // ---- Sync ----

    private void Sync()
    {
        var flow = Flow;
        IsMissing = flow is null;
        flow ??= new Flow(FlowId, "", []);
        FlowName = flow.Name;
        _arrangement = FlowLayout.Arrange(flow, id => _measured.GetValueOrDefault(id, DefaultPillSize));
        ExtentWidth = _arrangement.Extent.Width;
        ExtentHeight = _arrangement.Extent.Height;

        var problems = _session.Problems.Where(p => p.FlowId == FlowId && p.Severity == Severity.Error)
            .Select(p => NodeOf(flow, p.Path)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var nodes = flow.Nodes.GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);

        var boxes = _arrangement.Boxes.OrderBy(b => b.Card is null ? 0 : 1).ToList();
        for (var i = Nodes.Count - 1; i >= 0; i--)
        {
            if (!boxes.Any(b => b.Id == Nodes[i].Id))
                Nodes.RemoveAt(i);
        }
        for (var i = 0; i < boxes.Count; i++)
        {
            var box = boxes[i];
            var vm = Find(box.Id);
            if (vm is null)
            {
                vm = new CanvasNodeViewModel(box.Id);
                Nodes.Insert(i, vm);
            }
            else if (Nodes.IndexOf(vm) != i)
            {
                Nodes.Move(Nodes.IndexOf(vm), i);
            }
            vm.Update(nodes[box.Id], _session.Current, problems.Contains(box.Id), "");
            vm.Place(box);
            titles[box.Id] = vm.Title;
        }
        foreach (var vm in Nodes)
        {
            var waits = string.Join(", ", _arrangement.Edges.Where(e => e.To == vm.Id).Select(e => titles.GetValueOrDefault(e.From, e.From)));
            if (waits.Length > 0)
                vm.Update(nodes[vm.Id], _session.Current, problems.Contains(vm.Id), waits);
        }

        var mayFail = flow.Nodes.Where(n => n.OnFailure == FailurePolicy.Ignore).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        Edges.Clear();
        foreach (var route in _arrangement.Edges)
            Edges.Add(new CanvasEdgeViewModel(route, mayFail.Contains(route.To)));

        _selected.RemoveWhere(id => Find(id) is null);
        _selectedEdges.RemoveWhere(k => !Edges.Any(e => e.From == k.From && e.To == k.To));
        if (FocusedId is not null && Find(FocusedId) is null)
            FocusedId = null;
        SetFocus(FocusedId);
        ApplySelection();
        IsEmpty = Nodes.Count == 0;
        ApplyRun();
        Outline = BuildOutline();
        Problems = [.. _session.Problems.Where(p => p.FlowId == FlowId)
            .Select(p => (Problem: p, Node: NodeOf(flow, p.Path)))
            .Select(p => new ProblemItem(p.Problem.Severity, p.Problem.Message,
                p.Node is null ? "Flow settings" : titles.GetValueOrDefault(p.Node, p.Node), p.Node))
            .OrderBy(p => p.Severity)// Column, then top edge: a card member's Row counts within its card, not the column.
            .ThenBy(p => p.NodeId is null ? -1 : Find(p.NodeId)?.Column ?? int.MaxValue)
            .ThenBy(p => p.NodeId is null ? 0 : Find(p.NodeId)?.Y ?? 0)];
        OnPropertyChanged(nameof(Arrangement));
    }

    /// <summary>The step or card a problem path ("flows.f.nodes.bt.with.device") is about; null for the flow itself.</summary>
    private string? NodeOf(Flow flow, string path)
    {
        var prefix = $"flows.{FlowId}.nodes.";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return null;
        var rest = path[prefix.Length..];
        // Ids may contain dots ("audio.default"): the longest id the rest of the path starts with is the node.
        return flow.Nodes.Select(n => n.Id).OrderByDescending(id => id.Length)
            .FirstOrDefault(id => rest == id || rest.StartsWith(id + ".", StringComparison.Ordinal));
    }

    private List<OutlineItem> BuildOutline()
    {
        OutlineItem Item(CanvasNodeViewModel n, IReadOnlyList<OutlineItem> children) =>
            new(n.Id, n.Title, n.Summary, n.Glyph, n.AutomationName, children);
        return [.. Nodes.Where(n => n.Kind != CanvasNodeKind.Member).OrderBy(n => n.Column).ThenBy(n => n.Row)
            .Select(n => Item(n, [.. Nodes.Where(m => m.CardId == n.Id).OrderBy(m => m.Row).Select(m => Item(m, []))]))];
    }

    /// <summary>Fades these nodes (null or empty: none); the inspector's failure-policy preview.</summary>
    public void Preview(IReadOnlyCollection<string>? dimmed)
    {
        foreach (var node in Nodes)
            node.IsDimmed = dimmed?.Contains(node.Id) == true;
    }

    private void ApplySelection()
    {
        foreach (var node in Nodes)
            node.IsSelected = _selected.Contains(node.Id);
        foreach (var edge in Edges)
            edge.IsSelected = _selectedEdges.Contains((edge.From, edge.To)) || (_selected.Contains(edge.From) && _selected.Contains(edge.To));
        OnPropertyChanged(nameof(SelectedIds));
    }

    private void OnSessionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ConfigSession.Current) && !_editing && !Equals(Flow, _known))
        {
            // Reloaded from disk or changed on another page: undo would bring back a version made outside this canvas.
            _known = Flow;
            ClearHistory();
        }
        if (e.PropertyName == nameof(ConfigSession.Drafts))
            OnPropertyChanged(nameof(HasChanges));
        if (e.PropertyName is nameof(ConfigSession.Current) or nameof(ConfigSession.Problems))
            Sync();
        if (e.PropertyName == nameof(ConfigSession.CanEdit))
            OnPropertyChanged(nameof(CanEdit));
    }

    public void Dispose()
    {
        _session.PropertyChanged -= OnSessionChanged;
        _session.FlowRenamed -= OnFlowRenamed;
    }
}

/// <summary>Something the canvas says beside node <paramref name="At"/> after an edit went through.</summary>
public sealed record CanvasNote(string Text, string At);
