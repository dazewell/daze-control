using DazeControl.Core.Layout;

namespace DazeControl.Editor.ViewModels.Canvas;

/// <summary>
/// A free move while it is being dragged (D-119 b, D-120): from the arrangement the drop will commit with every step
/// frozen where it is (<see cref="FlowCanvasViewModel.BeginMove"/>) and an offset, the moved boxes (cards with their
/// members) and the routes of every line touching them, built with the same <see cref="FlowPlacement.Sides"/>,
/// <see cref="FlowPlacement.Port"/> and <see cref="EdgeCurve.For"/> code as the commit, so what is shown while
/// dragging is exactly what the drop gives. Lines between steps that don't move come from the frozen arrangement:
/// on a flow not placed by hand yet they lose their wrap lanes when the drag starts, never on release.
/// </summary>
public sealed class DragPreview
{
    private readonly Dictionary<string, NodeBox> _moved;
    private readonly Dictionary<string, Rect> _frozen;
    private readonly List<EdgeRoute> _touching;
    private readonly List<string> _topLevel;
    private readonly LayoutShape _shape;
    private readonly List<Rect> _movers, _still;
    private readonly double _columnGap, _rowGap;

    internal DragPreview(FlowArrangement frozen, IReadOnlyList<string> movers, LayoutOptions options)
    {
        Frozen = frozen;
        Lines = options.Lines;
        _shape = options.Shape;
        _columnGap = options.ColumnGap;
        _rowGap = options.RowGap;
        var top = new HashSet<string>(movers, StringComparer.Ordinal);
        _frozen = frozen.Boxes.ToDictionary(b => b.Id, b => b.Bounds, StringComparer.Ordinal);
        _moved = frozen.Boxes.Where(b => top.Contains(b.Card ?? b.Id)).ToDictionary(b => b.Id, StringComparer.Ordinal);
        _movers = [.. movers.Select(id => _moved.GetValueOrDefault(id)).OfType<NodeBox>().Select(b => b.Bounds)];
        _topLevel = [.. frozen.Boxes.Where(b => b.Card is null).Select(b => b.Id)];
        _still = [.. frozen.Boxes.Where(b => b.Card is null && !top.Contains(b.Id)).Select(b => b.Bounds)];
        StillBounds = _still.Count == 0 ? null : Union(_still);
        _touching = [.. frozen.Edges.Where(e => top.Contains(e.From) || top.Contains(e.To))];
        StaticEdges = [.. frozen.Edges.Where(e => !top.Contains(e.From) && !top.Contains(e.To))];
        Touching = _touching.Select(e => (e.From, e.To)).ToHashSet();
    }

    /// <summary>The arrangement with every top-level step placed where it is now: what a move commits, before the offset.</summary>
    public FlowArrangement Frozen { get; }

    public LayoutLines Lines { get; }

    /// <summary>Every id the move carries: the moved steps and cards and the cards' members.</summary>
    public IReadOnlyCollection<string> Moved => _moved.Keys;

    /// <summary>The lines between steps that don't move: drawn once when the drag starts.</summary>
    public IReadOnlyList<EdgeRoute> StaticEdges { get; }

    /// <summary>The lines that follow the drag, as (from, to).</summary>
    public IReadOnlySet<(string From, string To)> Touching { get; }

    /// <summary>The steps and cards that stay put, as one rect (auto-pan runs out with them, F-001); null when all move.</summary>
    public Rect? StillBounds { get; }

    /// <summary>
    /// The offset the drop commits for the pointer's travel (<see cref="MoveSnap"/>: the lead snaps to the grid or a
    /// column, the gaps are kept, above or left of the flow too), as <see cref="FlowCanvasViewModel.MoveBy"/> applies it.
    /// </summary>
    public Point Offset(double dx, double dy) =>
        _movers.Count == 0 ? default : MoveSnap.Offset(_movers, _still, dx, dy, _columnGap, _rowGap);

    private static Rect Union(List<Rect> rects)
    {
        double x = rects.Min(r => r.X), y = rects.Min(r => r.Y);
        return new Rect(x, y, rects.Max(r => r.Right) - x, rects.Max(r => r.Bottom) - y);
    }

    /// <summary>The moved boxes and the touching lines with the move at <paramref name="offset"/> (already snapped).</summary>
    public DragFrame At(Point offset)
    {
        var rects = new Dictionary<string, Rect>(_moved.Count, StringComparer.Ordinal);
        foreach (var (id, box) in _moved)
            rects[id] = box.Bounds with { X = box.Bounds.X + offset.X, Y = box.Bounds.Y + offset.Y };
        var top = new Dictionary<string, Rect>(_topLevel.Count, StringComparer.Ordinal);
        foreach (var id in _topLevel)
            top[id] = BoundsOf(id, rects);
        var routes = new List<EdgeRoute>(_touching.Count);
        foreach (var edge in _touching)
            routes.Add(FlowPlacement.Route(edge.From, edge.To, edge.When, top, _shape, Lines));
        // A line between steps that don't move detours round a moved step it would cross, and drops a detour the move
        // made needless (D-124): those follow the drag too, so the drop shows what the commit draws.
        var moved = _moved.Values.Where(b => b.Card is null).Select(b => rects[b.Id]).ToList();
        foreach (var edge in StaticEdges)
        {
            if (edge.Lane is null && !moved.Any(r => Near(EdgeCurve.For(edge, Lines), r)))
                continue;
            var route = FlowPlacement.Route(edge.From, edge.To, edge.When, top, _shape, Lines);
            if (route != edge)
                routes.Add(route);
        }
        return new DragFrame(offset, rects, routes, routes.ConvertAll(r => EdgeCurve.For(r, Lines)));
    }

    /// <summary>The curve of a line that doesn't move (from the frozen arrangement); null when it touches the move.</summary>
    public EdgeCurve? StaticCurve(string from, string to)
    {
        _staticCurves ??= StaticEdges.DistinctBy(e => (e.From, e.To)).ToDictionary(e => (e.From, e.To), e => EdgeCurve.For(e, Lines));
        return _staticCurves.TryGetValue((from, to), out var curve) ? curve : null;
    }

    private Dictionary<(string, string), EdgeCurve>? _staticCurves;

    /// <summary>Whether a box comes within reach of a line's bounding box (a cheap test before routing it again).</summary>
    private static bool Near(EdgeCurve curve, Rect r)
    {
        var points = curve.IsOrthogonal ? curve.Corners : [curve.Start, curve.Control1, curve.Control2, curve.End];
        return r.X <= points.Max(p => p.X) && r.Right >= points.Min(p => p.X) && r.Y <= points.Max(p => p.Y) && r.Bottom >= points.Min(p => p.Y);
    }

    private Rect BoundsOf(string id, Dictionary<string, Rect> rects) =>
        rects.TryGetValue(id, out var r) ? r : _frozen[id];
}

/// <param name="Offset">The snapped offset applied to every moved box.</param>
/// <param name="Rects">The moved boxes (and the members of moved cards) by id.</param>
/// <param name="Routes">The lines touching a moved box.</param>
/// <param name="Curves">Their curves, in the same order.</param>
public sealed record DragFrame(Point Offset, IReadOnlyDictionary<string, Rect> Rects, IReadOnlyList<EdgeRoute> Routes, IReadOnlyList<EdgeCurve> Curves);
