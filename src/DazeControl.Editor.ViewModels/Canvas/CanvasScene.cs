using DazeControl.Core.Layout;
using DazeControl.Core.Model;

namespace DazeControl.Editor.ViewModels.Canvas;

public enum SceneKind
{
    Step,
    Card,

    /// <summary>A step inside a card: no ports of its own (lines attach to the card).</summary>
    Member,
}

/// <summary>A step, card or member as laid out.</summary>
/// <param name="Content">What the box shows (the caller's token, compared with <see cref="object.Equals(object?, object?)"/>).</param>
public sealed record SceneItem(NodeBox Box, SceneKind Kind, object? Content)
{
    public string Id => Box.Id;

    public Rect Bounds => Box.Bounds;

    /// <summary>The card a member sits in; null for a top-level step or a card.</summary>
    public string? Card => Box.Card;

    public bool HasPorts => Kind != SceneKind.Member;
}

/// <summary>A line as laid out, with the curve it is drawn and hit-tested with and its style.</summary>
public sealed record SceneEdge(EdgeRoute Route, EdgeCurve Curve, EdgeStyle Style)
{
    public string From => Route.From;

    public string To => Route.To;

    public (string From, string To) Key => (Route.From, Route.To);

    /// <summary>A rect holding every point <see cref="EdgeCurve.DistanceTo"/> measures (the curve's control hull, its corners and the arrowhead).</summary>
    public Rect Reach { get; } = ReachOf(Curve);

    private static Rect ReachOf(EdgeCurve c)
    {
        double left = Math.Min(Math.Min(c.Start.X, c.End.X), Math.Min(Math.Min(c.Control1.X, c.Control2.X), c.Tip.X));
        double top = Math.Min(Math.Min(c.Start.Y, c.End.Y), Math.Min(Math.Min(c.Control1.Y, c.Control2.Y), c.Tip.Y));
        double right = Math.Max(Math.Max(c.Start.X, c.End.X), Math.Max(Math.Max(c.Control1.X, c.Control2.X), c.Tip.X));
        double bottom = Math.Max(Math.Max(c.Start.Y, c.End.Y), Math.Max(Math.Max(c.Control1.Y, c.Control2.Y), c.Tip.Y));
        if (c.IsOrthogonal)
        {
            foreach (var p in c.Corners)
            {
                (left, top) = (Math.Min(left, p.X), Math.Min(top, p.Y));
                (right, bottom) = (Math.Max(right, p.X), Math.Max(bottom, p.Y));
            }
        }
        // Rounding slack: a sampled Bézier point can sit a hair outside its hull.
        const double slack = 0.5;
        return new Rect(left - slack, top - slack, right - left + 2 * slack, bottom - top + 2 * slack);
    }
}

/// <summary>What <see cref="CanvasScene.Apply"/> lays the arrangement out with.</summary>
/// <param name="Cards">Ids of the top-level boxes that are cards.</param>
/// <param name="MayFail">Ids of the steps whose failure is ignored (lines into them are dashed).</param>
/// <param name="Content">Each box's content token; a different token reports the box in <see cref="SceneDiff.ContentChanged"/>.</param>
public sealed record SceneSetup(
    LayoutLines Lines = LayoutLines.Curved,
    LayoutShape Shape = LayoutShape.LeftToRight,
    IReadOnlySet<string>? Cards = null,
    IReadOnlySet<string>? MayFail = null,
    Func<string, object?>? Content = null)
{
    /// <summary>Lines leave by the bottom and enter by the top in the vertical shapes, else right and left.</summary>
    public bool PortsVertical => Shape is LayoutShape.TopToBottom or LayoutShape.Tree;
}

/// <summary>What one <see cref="CanvasScene.Apply"/> changed, so the canvas touches only that (D-119 a).</summary>
public sealed record SceneDiff(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Moved,
    IReadOnlyList<string> Resized,
    IReadOnlyList<string> ContentChanged,
    IReadOnlyList<(string From, string To)> EdgesAdded,
    IReadOnlyList<(string From, string To)> EdgesRemoved,
    IReadOnlyList<(string From, string To)> RoutesChanged,
    IReadOnlyList<(string From, string To)> StylesChanged,
    bool EdgeOrderChanged)
{
    public static SceneDiff Empty { get; } = new([], [], [], [], [], [], [], [], [], false);

    /// <summary>Any line was added, removed, re-routed, restyled or reordered: the edge figures need drawing again.</summary>
    public bool EdgesChanged => EdgesAdded.Count > 0 || EdgesRemoved.Count > 0 || RoutesChanged.Count > 0 || StylesChanged.Count > 0 || EdgeOrderChanged;

    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Moved.Count == 0 && Resized.Count == 0 && ContentChanged.Count == 0 && !EdgesChanged;
}

/// <summary>
/// The canvas's retained scene (D-119 a): every step, card and member by id (O(1) <see cref="Find"/>), every line with
/// its curve and style, a uniform-grid index for the hit tests, and a transient layer for what a gesture shows before
/// it commits (the live drag, <see cref="DragPreview"/>). <see cref="Apply"/> takes a new arrangement and returns
/// only what changed. Pure: no UI types, no timers; it changes only when told to.
/// </summary>
public sealed class CanvasScene
{
    private readonly List<SceneItem> _items = [];
    private readonly Dictionary<string, int> _itemAt = new(StringComparer.Ordinal);
    private readonly List<SceneEdge> _edges = [];
    private readonly Dictionary<(string From, string To), int> _edgeAt = [];
    private readonly SpatialGrid _boxGrid = new(), _edgeGrid = new(), _outGrid = new(), _inGrid = new();
    private readonly List<int> _hits = [];
    private readonly List<Point> _outPorts = [], _inPorts = [];
    private readonly Dictionary<(string From, string To), EdgeCurve> _live = [];

    /// <summary>Boxes in draw and hit order: top-level steps and cards first, then members.</summary>
    public IReadOnlyList<SceneItem> Items => _items;

    /// <summary>Lines in the arrangement's order.</summary>
    public IReadOnlyList<SceneEdge> Edges => _edges;

    public LayoutLines Lines { get; private set; } = LayoutLines.Curved;

    public PortSide OutSide { get; private set; } = PortSide.Right;

    public PortSide InSide { get; private set; } = PortSide.Left;

    public Size Extent { get; private set; }

    public SceneItem? Find(string id) => id is not null && _itemAt.TryGetValue(id, out var i) ? _items[i] : null;

    public SceneEdge? FindEdge(string from, string to) => _edgeAt.TryGetValue((from, to), out var i) ? _edges[i] : null;

    /// <summary>Where <paramref name="item"/>'s outgoing lines leave it.</summary>
    public Point OutPort(SceneItem item) => FlowPlacement.Port(item.Bounds, OutSide);

    /// <summary>Where <paramref name="item"/>'s incoming lines enter it.</summary>
    public Point InPort(SceneItem item) => FlowPlacement.Port(item.Bounds, InSide);

    public static EdgeStyle StyleOf(EdgeWhen when, bool intoMayFail) => when switch
    {
        EdgeWhen.Failure => EdgeStyle.IfFails,
        EdgeWhen.Always => EdgeStyle.Always,
        _ => intoMayFail ? EdgeStyle.IntoMayFail : EdgeStyle.Normal,
    };

    // ---- Apply ----

    /// <summary>Takes a new arrangement; returns what moved, resized, changed, came, went or was re-routed.</summary>
    public SceneDiff Apply(FlowArrangement arrangement, SceneSetup? setup = null)
    {
        ArgumentNullException.ThrowIfNull(arrangement);
        setup ??= new SceneSetup();
        Lines = setup.Lines;
        (OutSide, InSide) = setup.PortsVertical ? (PortSide.Bottom, PortSide.Top) : (PortSide.Right, PortSide.Left);
        Extent = arrangement.Extent;

        var oldItems = _items.ToDictionary(i => i.Id, StringComparer.Ordinal);
        List<string> added = [], moved = [], resized = [], content = [];
        _items.Clear();
        _itemAt.Clear();
        foreach (var box in arrangement.Boxes.DistinctBy(b => b.Id, StringComparer.Ordinal).OrderBy(b => b.Card is null ? 0 : 1))
        {
            var kind = box.Card is not null ? SceneKind.Member : setup.Cards?.Contains(box.Id) == true ? SceneKind.Card : SceneKind.Step;
            var item = new SceneItem(box, kind, setup.Content?.Invoke(box.Id));
            _itemAt[box.Id] = _items.Count;
            _items.Add(item);
            if (!oldItems.Remove(box.Id, out var old))
            {
                added.Add(box.Id);
                continue;
            }
            if (old.Bounds.X != box.Bounds.X || old.Bounds.Y != box.Bounds.Y)
                moved.Add(box.Id);
            if (old.Bounds.Width != box.Bounds.Width || old.Bounds.Height != box.Bounds.Height)
                resized.Add(box.Id);
            if (old.Kind != kind || old.Card != box.Card || !Equals(old.Content, item.Content))
                content.Add(box.Id);
        }
        var removed = oldItems.Keys.ToList();

        var oldEdges = _edges.ToList();
        var oldEdgeAt = new Dictionary<(string, string), int>(_edgeAt);
        List<(string, string)> edgesAdded = [], routes = [], styles = [];
        _edges.Clear();
        _edgeAt.Clear();
        var order = false;
        foreach (var route in arrangement.Edges)
        {
            var key = (route.From, route.To);
            if (_edgeAt.ContainsKey(key))
                continue;
            var edge = new SceneEdge(route, EdgeCurve.For(route, Lines), StyleOf(route.When, setup.MayFail?.Contains(route.To) == true));
            _edgeAt[key] = _edges.Count;
            _edges.Add(edge);
            if (!oldEdgeAt.Remove(key, out var was))
            {
                edgesAdded.Add(key);
                continue;
            }
            order |= was != _edges.Count - 1;
            if (oldEdges[was].Curve != edge.Curve)
                routes.Add(key);
            if (oldEdges[was].Style != edge.Style)
                styles.Add(key);
        }
        var edgesRemoved = oldEdgeAt.Keys.ToList();

        Reindex();
        return new SceneDiff(added, removed, moved, resized, content, edgesAdded, edgesRemoved, routes, styles,
            order && edgesAdded.Count == 0 && edgesRemoved.Count == 0);
    }

    private void Reindex()
    {
        _boxGrid.Reset(_items.Count);
        _outGrid.Reset(_items.Count);
        _inGrid.Reset(_items.Count);
        _outPorts.Clear();
        _inPorts.Clear();
        for (var i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            _boxGrid.Add(i, item.Bounds);
            Point o = OutPort(item), n = InPort(item);
            _outPorts.Add(o);
            _inPorts.Add(n);
            if (item.HasPorts)
            {
                _outGrid.Add(i, new Rect(o.X, o.Y, 0, 0));
                _inGrid.Add(i, new Rect(n.X, n.Y, 0, 0));
            }
        }
        _edgeGrid.Reset(_edges.Count);
        for (var i = 0; i < _edges.Count; i++)
            _edgeGrid.Add(i, _edges[i].Reach);
    }

    // ---- Hit tests (each returns exactly what a scan of every box or line would, in the same order) ----

    /// <summary>The topmost box under <paramref name="p"/>: a step or member before a card; else the earliest in <see cref="Items"/>.</summary>
    public SceneItem? HitTest(Point p)
    {
        _boxGrid.Query(new Rect(p.X, p.Y, 0, 0), _hits);
        var best = -1;
        foreach (var i in _hits)
        {
            if (!_items[i].Bounds.Contains(p))
                continue;
            if (best < 0 || Rank(i) < Rank(best) || (Rank(i) == Rank(best) && i < best))
                best = i;
        }
        return best < 0 ? null : _items[best];
    }

    private int Rank(int i) => _items[i].Kind == SceneKind.Card ? 1 : 0;

    /// <summary>The nearest line within <paramref name="band"/> DIP of <paramref name="p"/>; on a tie the earliest.</summary>
    public SceneEdge? HitTestEdge(Point p, double band = 12)
    {
        _edgeGrid.Query(Around(p, band), _hits);
        var (best, bestDistance) = (-1, double.MaxValue);
        foreach (var i in _hits)
        {
            var reach = _edges[i].Reach;
            if (p.X < reach.X - band || p.X > reach.Right + band || p.Y < reach.Y - band || p.Y > reach.Bottom + band)
                continue;
            var d = _edges[i].Curve.DistanceTo(p);
            if (d <= band && (d < bestDistance || (d == bestDistance && i < best)))
                (best, bestDistance) = (i, d);
        }
        return best < 0 ? null : _edges[best];
    }

    /// <summary>The step or card whose output port is within <paramref name="radius"/> of <paramref name="p"/>, nearest first.</summary>
    public SceneItem? OutPortAt(Point p, double radius) => PortAt(_outGrid, _outPorts, p, radius);

    /// <summary>The step or card whose input port is within <paramref name="radius"/> of <paramref name="p"/>, nearest first.</summary>
    public SceneItem? InPortAt(Point p, double radius) => PortAt(_inGrid, _inPorts, p, radius);

    private SceneItem? PortAt(SpatialGrid grid, List<Point> ports, Point p, double radius)
    {
        grid.Query(Around(p, radius), _hits);
        var (best, bestDistance) = (-1, double.MaxValue);
        foreach (var i in _hits)
        {
            var d = Distance(ports[i], p);
            if (d <= radius && (d < bestDistance || (d == bestDistance && i < best)))
                (best, bestDistance) = (i, d);
        }
        return best < 0 ? null : _items[best];
    }

    /// <summary>Every box touching <paramref name="area"/> (a marquee), in <see cref="Items"/> order.</summary>
    public IReadOnlyList<SceneItem> ItemsIn(Rect area)
    {
        _boxGrid.Query(area, _hits);
        _hits.Sort();
        return [.. _hits.Where(i => _items[i].Bounds.Intersects(area)).Select(i => _items[i])];
    }

    internal static double Distance(Point a, Point b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static Rect Around(Point p, double r) => new(p.X - r, p.Y - r, 2 * r, 2 * r);

    // ---- Transient layer: what a gesture shows before it commits ----

    /// <summary>The free move being dragged (D-119 b); null when none.</summary>
    public DragPreview? Drag { get; private set; }

    /// <summary>The drag at its last drawn offset; null before the first frame.</summary>
    public DragFrame? Frame { get; private set; }

    public bool IsDragging => Drag is not null;

    /// <summary>The curves of the lines that follow the drag now, by (from, to).</summary>
    public IReadOnlyDictionary<(string From, string To), EdgeCurve> LiveCurves => _live;

    public void BeginDrag(DragPreview preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        Drag = preview;
        Frame = null;
        _live.Clear();
    }

    /// <summary>Moves the drag to <paramref name="offset"/> (already snapped); false when that is where it is already.</summary>
    public bool DragTo(Point offset)
    {
        if (Drag is null || (Frame is not null && Frame.Offset == offset))
            return false;
        Frame = Drag.At(offset);
        _live.Clear();
        for (var i = 0; i < Frame.Routes.Count; i++)
            _live[(Frame.Routes[i].From, Frame.Routes[i].To)] = Frame.Curves[i];
        return true;
    }

    public void EndDrag()
    {
        Drag = null;
        Frame = null;
        _live.Clear();
    }

    /// <summary>Where a box is drawn now: its live rect while it is dragged, else where it is laid out.</summary>
    public Rect ShownBounds(string id) =>
        Frame is not null && Frame.Rects.TryGetValue(id, out var live) ? live : Find(id)?.Bounds ?? default;

    /// <summary>
    /// The curve a line is drawn with now: live while it follows a drag, from the frozen arrangement while another
    /// drag runs, else as laid out.
    /// </summary>
    public EdgeCurve ShownCurve(string from, string to) =>
        _live.TryGetValue((from, to), out var live) ? live
        : Drag?.StaticCurve(from, to) ?? FindEdge(from, to)?.Curve ?? default;
}
