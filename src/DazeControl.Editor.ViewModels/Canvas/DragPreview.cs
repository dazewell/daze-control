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
    private readonly LayoutShape _shape;
    private readonly Point _lead, _topLeft;
    private readonly double _padding;

    internal DragPreview(FlowArrangement frozen, IReadOnlyList<string> movers, LayoutOptions options)
    {
        Frozen = frozen;
        Lines = options.Lines;
        _shape = options.Shape;
        _padding = options.Padding;
        var top = new HashSet<string>(movers, StringComparer.Ordinal);
        _frozen = frozen.Boxes.ToDictionary(b => b.Id, b => b.Bounds, StringComparer.Ordinal);
        _moved = frozen.Boxes.Where(b => top.Contains(b.Card ?? b.Id)).ToDictionary(b => b.Id, StringComparer.Ordinal);
        var leaders = movers.Select(id => _moved.GetValueOrDefault(id)).OfType<NodeBox>().ToList();
        _lead = leaders.Count == 0 ? default : new Point(leaders[0].Bounds.X, leaders[0].Bounds.Y);
        _topLeft = leaders.Count == 0 ? default : new Point(leaders.Min(b => b.Bounds.X), leaders.Min(b => b.Bounds.Y));
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

    /// <summary>
    /// The offset the drop commits for the pointer's travel (<see cref="MoveSnap"/>: the lead snaps to the grid,
    /// nothing above or left of the padding), as <see cref="FlowCanvasViewModel.MoveBy"/> applies it.
    /// </summary>
    public Point Offset(double dx, double dy) =>
        _moved.Count == 0 ? default : MoveSnap.Offset(_lead, _topLeft, dx, dy, _padding);

    /// <summary>The moved boxes and the touching lines with the move at <paramref name="offset"/> (already snapped).</summary>
    public DragFrame At(Point offset)
    {
        var rects = new Dictionary<string, Rect>(_moved.Count, StringComparer.Ordinal);
        foreach (var (id, box) in _moved)
            rects[id] = box.Bounds with { X = box.Bounds.X + offset.X, Y = box.Bounds.Y + offset.Y };
        var routes = new List<EdgeRoute>(_touching.Count);
        foreach (var edge in _touching)
        {
            var (a, b) = (BoundsOf(edge.From, rects), BoundsOf(edge.To, rects));
            var (startSide, endSide) = FlowPlacement.Sides(a, b, _shape);
            routes.Add(new EdgeRoute(edge.From, edge.To, edge.When, FlowPlacement.Port(a, startSide), FlowPlacement.Port(b, endSide), null, startSide, endSide));
        }
        return new DragFrame(offset, rects, routes, routes.ConvertAll(r => EdgeCurve.For(r, Lines)));
    }

    /// <summary>The curve of a line that doesn't move (from the frozen arrangement); null when it touches the move.</summary>
    public EdgeCurve? StaticCurve(string from, string to) =>
        StaticEdges.FirstOrDefault(e => e.From == from && e.To == to) is { } route ? EdgeCurve.For(route, Lines) : null;

    private Rect BoundsOf(string id, Dictionary<string, Rect> rects) =>
        rects.TryGetValue(id, out var r) ? r : _frozen[id];
}

/// <param name="Offset">The snapped offset applied to every moved box.</param>
/// <param name="Rects">The moved boxes (and the members of moved cards) by id.</param>
/// <param name="Routes">The lines touching a moved box.</param>
/// <param name="Curves">Their curves, in the same order.</param>
public sealed record DragFrame(Point Offset, IReadOnlyDictionary<string, Rect> Rects, IReadOnlyList<EdgeRoute> Routes, IReadOnlyList<EdgeCurve> Curves);
