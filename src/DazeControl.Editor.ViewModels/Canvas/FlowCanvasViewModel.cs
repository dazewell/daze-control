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
    private readonly EditorSettingsStore? _settings;
    private FlowViewSettings _localView = new(); // without a store (tests, a failed start): this session only
    private readonly List<UndoEntry> _undo = [], _redo = [];

    /// <summary>An undo step: the flow before an edit, or (D-115) this flow's view choices before a move or Arrange.</summary>
    /// <summary>A flow edit (<paramref name="View"/> null), a view change, or both at once (<paramref name="WithFlow"/>: a library drop that places the new step).</summary>
    private sealed record UndoEntry(Flow Flow, FlowViewSettings? View, bool WithFlow = false);
    private FlowArrangement _arrangement = new([], [], [], new Size(96, 96));
    private Flow? _known;
    private bool _editing;

    /// <summary>Undo steps kept per open flow.</summary>
    public const int HistoryLimit = 100;

    public FlowCanvasViewModel(ConfigSession session, string flowId, CanvasClipboard? clipboard = null, EditorSettingsStore? settings = null)
    {
        _session = session;
        _clipboard = clipboard ?? new CanvasClipboard();
        _settings = settings;
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
        // The view choices follow (D-075; the shell's own call is then a no-op). No Sync here: the session may not
        // hold the new id yet, and a flow that looks missing closes the editor; its Current change syncs.
        _settings?.RenameFlow(oldId, newId);
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
    [NotifyPropertyChangedFor(nameof(CanEdit), nameof(CanUntangle))]
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

    /// <summary>Any flow of the open config by id (a called or calling flow's names for live runs).</summary>
    internal Flow? FlowById(string id) => _session.Current.Flows.FirstOrDefault(f => f.Id == id);

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

    /// <summary>The step type when the selection is one step: the menu's "Select all Set default device steps" (D-084).</summary>
    public string? SelectedStepType => _selected.Count == 1 && Flow?.FindNode(_selected.First()) is StepNode step ? step.Step : null;

    /// <summary>Selects every step of <paramref name="stepType"/> in the flow (never cards).</summary>
    public void SelectAllOfType(string stepType)
    {
        if (Flow is not { } flow)
            return;
        SelectOnly(flow.Nodes.OfType<StepNode>().Where(s => s.Step == stepType).Select(s => s.Id));
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
        var o = LayoutOptions;
        var columns = o.Shape == LayoutShape.LeftToRight && !IsPlacedByHand;
        CanvasNodeViewModel? to = !columns ? Nearest(from, move) : move switch
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

    /// <summary>
    /// Arrow keys in any other shape or with steps placed by hand (D-115): the nearest step or card that is drawn in
    /// that direction (within 60° of it), as seen on screen. A card's own members count, so Down from a card walks
    /// into it; other cards' members are reached through their card.
    /// </summary>
    private CanvasNodeViewModel? Nearest(CanvasNodeViewModel from, FocusMove move)
    {
        var (dx, dy) = move switch
        {
            FocusMove.Left => (-1, 0),
            FocusMove.Right => (1, 0),
            FocusMove.Up => (0, -1),
            _ => (0, 1),
        };
        var fx = from.X + from.Width / 2;
        var fy = from.Bounds.CenterY;
        return Nodes
            .Where(n => n.Id != from.Id && (n.CardId is null || n.CardId == from.Id || n.CardId == from.CardId))
            .Select(n => (Node: n, X: n.X + n.Width / 2 - fx, Y: n.Bounds.CenterY - fy))
            .Select(c => (c.Node, Ahead: c.X * dx + c.Y * dy, Side: Math.Abs(c.X * dy + c.Y * dx)))
            .Where(c => c.Ahead > 0.5 && c.Side <= c.Ahead * 1.75)
            // On a tie a member beats its card: a member centred on its card would otherwise never be reached.
            .OrderBy(c => c.Ahead + 2 * c.Side).ThenBy(c => c.Node.CardId is null)
            .Select(c => c.Node).FirstOrDefault();
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

    /// <summary>The vertical shapes (top to bottom, tree) put the output port under a step and the input port on top (D-115).</summary>
    public bool PortsVertical => LayoutChoices.Shape is LayoutShape.TopToBottom or LayoutShape.Tree;

    public PortSide OutSide => PortsVertical ? PortSide.Bottom : PortSide.Right;

    public PortSide InSide => PortsVertical ? PortSide.Top : PortSide.Left;

    /// <summary>Where this flow's lines leave <paramref name="node"/> (right, or bottom in the vertical shapes).</summary>
    public Point OutPortOf(CanvasNodeViewModel node) => FlowPlacement.Port(node.Bounds, OutSide);

    /// <summary>Where this flow's lines enter <paramref name="node"/> (left, or top in the vertical shapes).</summary>
    public Point InPortOf(CanvasNodeViewModel node) => FlowPlacement.Port(node.Bounds, InSide);

    /// <summary>The step or card whose output port is under <paramref name="p"/>; members have none (ports on the card).</summary>
    public CanvasNodeViewModel? OutPortAt(Point p, double radius = PortRadius) =>
        Nodes.Where(n => n.Kind != CanvasNodeKind.Member)
            .Select(n => (Node: n, Distance: Distance(OutPortOf(n), p)))
            .Where(x => x.Distance <= radius).OrderBy(x => x.Distance).Select(x => x.Node).FirstOrDefault();

    /// <summary>
    /// Where a connect drag released at <paramref name="p"/> ends: the node under it (a member counts as its card), else
    /// the node whose input port is within <paramref name="radius"/> (the dot overhangs the node's left edge).
    /// </summary>
    public CanvasNodeViewModel? ConnectTargetAt(Point p, double radius = PortRadius)
    {
        var node = HitTest(p) ?? Nodes.Where(n => n.Kind != CanvasNodeKind.Member)
            .Select(n => (Node: n, Distance: Distance(InPortOf(n), p)))
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
        "If this fails, its fallback runs and the flow carries on, so it's set to \"Skip what follows\" now.";

    /// <summary>
    /// A step or card with an "if it fails" link and "Stop the flow" runs the fallback and goes on (D-024), which is
    /// what "Skip what follows" says, so the policy shows that (D-059). Skip and Run the next steps anyway stay as chosen.
    /// </summary>
    public static Flow WithFallbackPolicy(Flow flow, string source, ValueDictionary<StepDefault>? defaults = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var top = FlowGraph.TopLevel(flow, source);
        var cardOf = FlowLayout.CardOf(flow);
        // The effective policy decides (D-080): a step that inherits "Stop" gets Skip set on itself.
        if (flow.FindNode(top) is not { } node || EffectivePolicy(node, defaults) != FailurePolicy.Stop
            || !FlowLayout.Edges(flow, cardOf).Any(e => e.From == top && e.When == EdgeWhen.Failure))
            return flow;
        return flow with { Nodes = [.. flow.Nodes.Select(n => n.Id == top ? n with { OnFailure = FailurePolicy.Skip } : n)] };
    }

    private static FailurePolicy EffectivePolicy(Node node, ValueDictionary<StepDefault>? defaults) =>
        node.OnFailure
        ?? (node is StepNode step && defaults?.TryGetValue(step.Step, out var d) == true ? d.OnFailure : null)
        ?? FailurePolicy.Stop;

    /// <summary>One undo step: <paramref name="change"/> plus the fallback policy of <paramref name="source"/>; noted.</summary>
    private void EditFallback(string source, Func<Flow, Flow> change)
    {
        var switched = false;
        Edit(flow =>
        {
            var changed = change(flow);
            var next = WithFallbackPolicy(changed, source, _session.Current.Settings.StepDefaults);
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
        if (p.X < target.X + target.Width / 3)
            return DropZone.RunBefore;
        // A card's body is all "run together"; a pill keeps thin top/bottom bands for reordering.
        var band = target.Kind == CanvasNodeKind.Card ? 0 : target.Height / 4;
        return p.Y >= target.Y + band && p.Y <= target.Y + target.Height - band ? DropZone.RunTogether : DropZone.None;
    }

    /// <summary>Drop <paramref name="dragged"/> on <paramref name="target"/>'s zone; a refusal changes nothing.</summary>
    public ConnectRefusal Drop(string dragged, string target, DropZone zone)
    {
        if (Flow is not { } flow || zone == DropZone.None)
            return ConnectRefusal.None;
        var (next, refusal, droppedFallback) = ZoneEdit(flow, dragged, target, zone);
        if (refusal == ConnectRefusal.None && next != flow)
        {
            Edit(_ => next);
            if (droppedFallback)
                Note(DroppedFallbackNote, FlowGraph.TopLevel(next, dragged));
        }
        return refusal;
    }

    private static GroupResult ZoneEdit(Flow flow, string dragged, string target, DropZone zone) => zone switch
    {
        DropZone.RunAfter => FlowEdits.RunAfter(flow, dragged, target),
        DropZone.RunBefore => FlowEdits.RunBefore(flow, dragged, target),
        _ => FlowEdits.RunTogether(flow, dragged, target),
    };

    /// <summary>Steps and cards <paramref name="step"/> could run before: the keyboard route of the left-third drop (D-083).</summary>
    public IReadOnlyList<CanvasNodeViewModel> RunBeforeCandidates(string step)
    {
        if (Flow is not { } flow || !CanEdit || flow.FindNode(step) is null)
            return [];
        return [.. Nodes.Where(n => n.Kind != CanvasNodeKind.Member && n.Id != step
                && FlowEdits.RunBefore(flow, step, n.Id) is { Refusal: ConnectRefusal.None } r && r.Flow != flow)
            .OrderBy(n => n.Column).ThenBy(n => n.Row)];
    }

    /// <summary>"Run before…": the same edit as dropping on <paramref name="target"/>'s left third.</summary>
    public ConnectRefusal RunBefore(string step, string target) => Drop(step, target, DropZone.RunBefore);

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

    // ---- Layout menu (D-067) ----

    /// <summary>This flow's layout choices on this PC (editor.json, D-068); not a flow edit.</summary>
    public FlowViewSettings LayoutChoices =>
        _settings is null ? _localView : _settings.Settings.Flows.GetValueOrDefault(FlowId) ?? new FlowViewSettings();

    public LayoutOptions LayoutOptions => LayoutChoices.ToOptions();

    /// <summary>Changes this flow's layout choices and lays it out again at once; a default value isn't stored.</summary>
    public void SetLayout(int? wrapAfter = null, LayoutSpacing? spacing = null, LayoutAlign? align = null, LayoutLines? lines = null)
    {
        void Change(FlowViewSettings v)
        {
            if (wrapAfter is { } w)
                v.WrapAfter = w == LayoutOptions.DefaultWrapAfter ? null : Math.Max(0, w);
            if (spacing is { } s)
                v.Spacing = s == LayoutSpacing.Normal ? null : s;
            if (align is { } a)
                v.Align = a == LayoutAlign.Centre ? null : a;
            if (lines is { } l)
                v.Lines = l == LayoutLines.Curved ? null : l;
        }
        ChangeView(Change, undoable: false);
    }

    /// <summary>Layout › Reset layout: this flow back to the default choices; keys a newer editor wrote are kept.</summary>
    /// <remarks>It can throw away a hand placement, so it is an undo step (D-115).</remarks>
    public void ResetLayout()
    {
        if (LayoutChoices.IsDefault)
            return;
        ChangeView(v => (v.WrapAfter, v.Spacing, v.Align, v.Lines, v.Shape, v.Placed) = (null, null, null, null, null, null), undoable: true);
    }

    // ---- Arrange and hand placement (D-115) ----

    /// <summary>Some steps were placed by hand: the shape no longer lays the flow out, and Wrap, Spacing and Line up wait.</summary>
    [ObservableProperty]
    public partial bool IsPlacedByHand { get; private set; }

    /// <summary>Layout › Arrange: lays every step out afresh in <paramref name="shape"/>, letting go of hand placement; an undo step.</summary>
    public void Arrange(LayoutShape shape)
    {
        var choices = LayoutChoices;
        if ((choices.Shape ?? LayoutShape.LeftToRight) == shape && !choices.IsPlacedByHand)
            return;
        ChangeView(v => (v.Shape, v.Placed) = (shape == LayoutShape.LeftToRight ? null : shape, null), undoable: true);
    }

    /// <summary>
    /// A drag to empty canvas: the steps and cards in <paramref name="ids"/> (members count as their card) move by
    /// (<paramref name="dx"/>, <paramref name="dy"/>), snapped to the grid, never above or left of the padding. The
    /// first move freezes every other step where it is, so nothing else shifts. One undo step; false when nothing moved.
    /// </summary>
    public bool MoveBy(IEnumerable<string> ids, double dx, double dy)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (!CanEdit || IsRunLocked)
            return false;
        var moving = ids.Select(Find).OfType<CanvasNodeViewModel>()
            .Select(n => n.CardId is { } card ? Find(card) : n).OfType<CanvasNodeViewModel>()
            .DistinctBy(n => n.Id).ToList();
        if (moving.Count == 0)
            return false;
        // The first (the one under the pointer) snaps to the grid; the rest keep their distance from it.
        var padding = LayoutOptions.Padding;
        var lead = moving[0];
        dx = FlowPlacement.Snap(lead.X + dx) - lead.X;
        dy = FlowPlacement.Snap(lead.Y + dy) - lead.Y;
        dx = Math.Max(dx, padding - moving.Min(n => n.X));
        dy = Math.Max(dy, padding - moving.Min(n => n.Y));
        var to = moving.ToDictionary(n => n.Id, n => new Point(n.X + dx, n.Y + dy), StringComparer.Ordinal);
        if (moving.All(n => n.X == to[n.Id].X && n.Y == to[n.Id].Y))
            return false;
        var everything = Nodes.Where(n => n.CardId is null).ToDictionary(n => n.Id, n => new Point(n.X, n.Y), StringComparer.Ordinal);
        ChangeView(v =>
        {
            v.Placed = everything; // rebuilt from what is on the canvas, so deleted steps drop out
            foreach (var (id, at) in to)
                v.Placed[id] = at;
        }, undoable: true);
        return true;
    }

    /// <summary>Changes this flow's view choices in editor.json (or this session's copy) and lays it out again.</summary>
    private void ChangeView(Action<FlowViewSettings> change, bool undoable)
    {
        var before = LayoutChoices.Copy();
        if (_settings is null)
            change(_localView);
        else
        {
            var id = FlowId;
            _settings.Update(s =>
            {
                var view = s.Flows.GetValueOrDefault(id) ?? new FlowViewSettings();
                change(view);
                if (view.IsDefault)
                    s.Flows.Remove(id);
                else
                    s.Flows[id] = view;
            });
        }
        if (undoable && Flow is { } flow)
        {
            Push(_undo, new UndoEntry(flow, before));
            _redo.Clear();
            UpdateHistory();
        }
        Sync();
    }

    private (Flow? Flow, LayoutAlign Align, UntangleState State) _untangle;

    /// <summary>
    /// Whether Untangle lines would change anything (X-006: it's off with a reason when it can't). Worked out when
    /// asked (the Layout menu opening), once per flow version and Line up, not on every Sync: it runs the sweeps.
    /// </summary>
    public UntangleState UntangleState
    {
        get
        {
            var flow = Flow;
            var align = LayoutOptions.Align;
            if (flow is null)
                return UntangleState.NoCrossings;
            if (!ReferenceEquals(_untangle.Flow, flow) || _untangle.Align != align)
                _untangle = (flow, align, FlowTidy.Check(flow, align));
            return _untangle.State;
        }
    }

    public bool CanUntangle => CanEdit && UntangleState == UntangleState.Helps;

    /// <summary>Why Untangle lines is off (UX #63); null while it can help.</summary>
    public string? UntangleReason => UntangleState switch
    {
        UntangleState.NoCrossings => "No lines cross",
        UntangleState.NoBetterOrder => "Can't untangle these any further",
        _ => null,
    };

    /// <summary>Layout › "Untangle lines": reorders columns to cut crossings, a flow edit with undo; false when nothing changed.</summary>
    public bool Untangle()
    {
        if (!CanEdit || Flow is not { } flow || FlowTidy.Tidy(flow, LayoutOptions.Align) is var tidy && ReferenceEquals(tidy, flow))
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
        Push(_undo, new UndoEntry(before, null));
        _redo.Clear();
        UpdateHistory();
    }

    /// <summary>Ctrl+Z.</summary>
    public void Undo() => Step(_undo, _redo);

    /// <summary>Ctrl+Y / Ctrl+Shift+Z.</summary>
    public void Redo() => Step(_redo, _undo);

    private void Step(List<UndoEntry> from, List<UndoEntry> to)
    {
        if (from.Count == 0 || Flow is not { } current || !CanEdit)
            return;
        var target = from[^1];
        from.RemoveAt(from.Count - 1);
        if (target.View is { } view)
        {
            Push(to, new UndoEntry(current, LayoutChoices.Copy(), target.WithFlow));
            if (target.WithFlow)
                Replace(_ => target.Flow);
            var copy = view.Copy();
            ChangeView(v => (v.WrapAfter, v.Spacing, v.Align, v.Lines, v.Shape, v.Placed, v.Other) =
                (copy.WrapAfter, copy.Spacing, copy.Align, copy.Lines, copy.Shape, copy.Placed, copy.Other), undoable: false);
        }
        else
        {
            Replace(_ => target.Flow);
            Push(to, new UndoEntry(current, null));
        }
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

    private static void Push(List<UndoEntry> stack, UndoEntry entry)
    {
        stack.Add(entry);
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

    /// <summary>
    /// Where a step of <paramref name="size"/> dropped from the library at content point <paramref name="p"/> lands on
    /// empty canvas: centred on the point, its top left snapped to the grid, never above or left of the padding (D-120).
    /// The canvas draws its dashed landing outline from this, so the outline is exactly where the step goes.
    /// </summary>
    public Rect LandingAt(Point p, Size size)
    {
        var padding = LayoutOptions.Padding;
        return new Rect(
            Math.Max(padding, FlowPlacement.Snap(p.X - size.Width / 2)),
            Math.Max(padding, FlowPlacement.Snap(p.Y - size.Height / 2)),
            size.Width, size.Height);
    }

    /// <summary>
    /// A step dropped from the library at content point <paramref name="p"/> (D-120). On <paramref name="targetId"/>'s
    /// <paramref name="zone"/> it is the step drag's edit (Run before / after / together); otherwise it lands centred
    /// under the point as a hand placement: the first one freezes every other step where it is (D-115). One undo step
    /// either way; selected. A refused zone adds nothing.
    /// </summary>
    public (string? Id, ConnectRefusal Refusal) AddStepAt(string step, Point p, string? targetId = null, DropZone zone = DropZone.None)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (Flow is not { } flow || !CanEdit || IsRunLocked)
            return (null, ConnectRefusal.None);
        var (added, id) = FlowCommands.Add(flow, step, []);
        if (targetId is not null && zone != DropZone.None && Find(targetId) is not null)
        {
            var (next, refusal, _) = ZoneEdit(added, id, targetId, zone);
            if (refusal != ConnectRefusal.None)
                return (null, refusal);
            Edit(_ => next);
            SelectOnly([id]);
            return (id, ConnectRefusal.None);
        }
        var at = LandingAt(p, DefaultPillSize);
        var before = LayoutChoices.Copy();
        var everything = Nodes.Where(n => n.CardId is null).ToDictionary(n => n.Id, n => new Point(n.X, n.Y), StringComparer.Ordinal);
        Replace(_ => added);
        ChangeView(v =>
        {
            v.Placed = everything; // as in MoveBy: what is on the canvas now, so nothing else shifts
            v.Placed[id] = new Point(at.X, at.Y);
        }, undoable: false);
        Push(_undo, new UndoEntry(flow, before, WithFlow: true));
        _redo.Clear();
        UpdateHistory();
        SelectOnly([id]);
        return (id, ConnectRefusal.None);
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
        var options = LayoutOptions;
        _arrangement = FlowPlacement.Arrange(flow, id => _measured.GetValueOrDefault(id, DefaultPillSize), options);
        IsPlacedByHand = FlowPlacement.HasPlaced(flow, options.Placed);
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
            vm.Update(nodes[box.Id], _session.Current, problems.Contains(box.Id));
            vm.Place(box);
            titles[box.Id] = vm.Title;
        }
        // Once every title is known; a node whose waits didn't change raises nothing (T21.1: a drop used to re-raise
        // every node's name, each a full pill refresh).
        foreach (var vm in Nodes)
            vm.SetWaitsFor(string.Join(", ", _arrangement.Edges.Where(e => e.To == vm.Id).Select(e => titles.GetValueOrDefault(e.From, e.From))));

        var mayFail = flow.Nodes.Where(n => EffectivePolicy(n, _session.Current.Settings.StepDefaults) == FailurePolicy.Ignore).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        Edges.Clear();
        foreach (var route in _arrangement.Edges)
            Edges.Add(new CanvasEdgeViewModel(route, mayFail.Contains(route.To), options.Lines));

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
        {
            OnPropertyChanged(nameof(CanEdit));
            OnPropertyChanged(nameof(CanUntangle));
        }
    }

    public void Dispose()
    {
        _session.PropertyChanged -= OnSessionChanged;
        _session.FlowRenamed -= OnFlowRenamed;
    }
}

/// <summary>Something the canvas says beside node <paramref name="At"/> after an edit went through.</summary>
public sealed record CanvasNote(string Text, string At);
