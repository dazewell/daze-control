using DazeControl.Core.Model;

namespace DazeControl.Core.Layout;

/// <summary>
/// The canvas layout with Arrange and hand placement (D-115). Left to right with nothing placed by hand is exactly
/// <see cref="FlowLayout.Arrange"/>. Every other shape starts from it (sizes, cards, run order) and moves the
/// top-level boxes; card members move with their card. Steps placed by hand stay where they were put; a step that has
/// no place yet (just added or pasted) lands beside the step it runs after, pushed down until it overlaps nothing.
/// Deterministic: the same flow, sizes and choices give the same picture, so nothing jumps between opens.
/// </summary>
public static class FlowPlacement
{
    /// <summary>Snap for hand placement, in DIP (D-115).</summary>
    public const double Grid = 8;

    public static double Snap(double value) => Math.Round(value / Grid) * Grid;

    public static FlowArrangement Arrange(Flow flow, Func<string, Size> measure, LayoutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(measure);
        var o = options ?? LayoutOptions.Default;
        var placed = o.Placed is { Count: > 0 } p ? p : null;
        if (o.Shape == LayoutShape.LeftToRight && placed is null)
            return FlowLayout.Arrange(flow, measure, o);

        // Wrapped rows only make sense left to right; with hand placement the wrap lanes no longer fit the boxes.
        var auto = FlowLayout.Arrange(flow, measure, o.Shape == LayoutShape.LeftToRight ? o : o with { WrapAfter = 0 });
        var top = auto.Boxes.Where(b => b.Card is null).ToDictionary(b => b.Id, b => b.Bounds, StringComparer.Ordinal);
        var order = auto.Columns.SelectMany(c => c).Where(top.ContainsKey).ToList();
        var cardOf = FlowLayout.CardOf(flow);
        var edges = FlowLayout.Edges(flow, cardOf).Where(e => top.ContainsKey(e.From) && top.ContainsKey(e.To)).ToList();
        var layers = FlowLayout.Layers(flow);

        var at = o.Shape switch
        {
            LayoutShape.TopToBottom => Rows(auto.Columns, top, o),
            LayoutShape.Tree => Tree(order, top, edges, layers, o),
            LayoutShape.Circle => Circle(Preorder(order, Children(order, edges, layers)), top, o),
            _ => top.ToDictionary(kv => kv.Key, kv => new Point(kv.Value.X, kv.Value.Y), StringComparer.Ordinal),
        };
        at = Pad(at, o.Padding);
        if (placed is not null)
            at = Pad(Place(order, top, edges, layers, at, placed, o), o.Padding);

        var boxes = new List<NodeBox>(auto.Boxes.Count);
        foreach (var box in auto.Boxes)
        {
            var owner = box.Card ?? box.Id;
            if (!top.TryGetValue(owner, out var was) || !at.TryGetValue(owner, out var now))
                continue;
            boxes.Add(box with { Bounds = box.Bounds with { X = box.Bounds.X + now.X - was.X, Y = box.Bounds.Y + now.Y - was.Y } });
        }
        var final = boxes.Where(b => b.Card is null).ToDictionary(b => b.Id, b => b.Bounds, StringComparer.Ordinal);
        var routes = new List<EdgeRoute>(edges.Count);
        foreach (var (from, to, when) in edges)
        {
            var (a, b) = (final[from], final[to]);
            var (startSide, endSide) = Sides(a, b, o.Shape);
            routes.Add(new EdgeRoute(from, to, when, Port(a, startSide), Port(b, endSide), null, startSide, endSide));
        }
        var right = boxes.Count == 0 ? o.Padding : boxes.Max(b => b.Bounds.Right);
        var bottom = boxes.Count == 0 ? o.Padding : boxes.Max(b => b.Bounds.Bottom);
        return new FlowArrangement(boxes, routes, auto.Columns, new Size(right + o.Padding, bottom + o.Padding));
    }

    /// <summary>
    /// The sides a line uses: the shape's own direction while the target lies ahead in it (right → left, or bottom →
    /// top for the vertical shapes), else the two sides that face each other across the wider gap.
    /// </summary>
    public static (PortSide Start, PortSide End) Sides(Rect a, Rect b, LayoutShape shape)
    {
        if (shape is LayoutShape.TopToBottom or LayoutShape.Tree && b.Y >= a.Bottom)
            return (PortSide.Bottom, PortSide.Top);
        if (shape == LayoutShape.LeftToRight && b.X >= a.Right)
            return (PortSide.Right, PortSide.Left);
        var dx = b.X + b.Width / 2 - (a.X + a.Width / 2);
        var dy = b.CenterY - a.CenterY;
        var gapX = dx >= 0 ? b.X - a.Right : a.X - b.Right;
        var gapY = dy >= 0 ? b.Y - a.Bottom : a.Y - b.Bottom;
        if (gapX >= gapY)
            return dx >= 0 ? (PortSide.Right, PortSide.Left) : (PortSide.Left, PortSide.Right);
        return dy >= 0 ? (PortSide.Bottom, PortSide.Top) : (PortSide.Top, PortSide.Bottom);
    }

    /// <summary>The middle of a box's side.</summary>
    public static Point Port(Rect r, PortSide side) => side switch
    {
        PortSide.Left => new(r.X, r.CenterY),
        PortSide.Top => new(r.X + r.Width / 2, r.Y),
        PortSide.Bottom => new(r.X + r.Width / 2, r.Bottom),
        _ => new(r.Right, r.CenterY),
    };

    /// <summary>Top to bottom: one row per column of the left-to-right layout, rows centred on the widest.</summary>
    private static Dictionary<string, Point> Rows(IReadOnlyList<IReadOnlyList<string>> columns, Dictionary<string, Rect> top, LayoutOptions o)
    {
        var gap = SiblingGap(o);
        var rows = columns.Select(c => c.Where(top.ContainsKey).ToList()).Where(r => r.Count > 0).ToList();
        var widths = rows.Select(r => r.Sum(id => top[id].Width) + gap * (r.Count - 1)).ToList();
        var widest = widths.DefaultIfEmpty(0).Max();
        var at = new Dictionary<string, Point>(StringComparer.Ordinal);
        var y = 0.0;
        for (var r = 0; r < rows.Count; r++)
        {
            var x = (widest - widths[r]) / 2;
            var height = rows[r].Max(id => top[id].Height);
            foreach (var id in rows[r])
            {
                at[id] = new Point(x, y + (height - top[id].Height) / 2);
                x += top[id].Width + gap;
            }
            y += height + LevelGap(o);
        }
        return at;
    }

    /// <summary>
    /// A tidy tree, top down: each step hangs under the step it waits for that runs last (its layer is one more, so
    /// the levels are the left-to-right columns), parents centred over their children, separate trees side by side.
    /// </summary>
    private static Dictionary<string, Point> Tree(List<string> order, Dictionary<string, Rect> top,
        List<(string From, string To, EdgeWhen When)> edges, Dictionary<string, int> layers, LayoutOptions o)
    {
        var children = Children(order, edges, layers);
        var hasParent = children.Values.SelectMany(c => c).ToHashSet(StringComparer.Ordinal);

        var levels = order.Select(id => layers.GetValueOrDefault(id)).DefaultIfEmpty(0).Max() + 1;
        var levelHeight = new double[levels];
        foreach (var id in order)
            levelHeight[layers.GetValueOrDefault(id)] = Math.Max(levelHeight[layers.GetValueOrDefault(id)], top[id].Height);
        var levelY = new double[levels];
        for (var l = 1; l < levels; l++)
            levelY[l] = levelY[l - 1] + levelHeight[l - 1] + LevelGap(o);

        var gap = SiblingGap(o);
        var span = new Dictionary<string, double>(StringComparer.Ordinal);
        double Span(string id)
        {
            if (span.TryGetValue(id, out var known))
                return known;
            var kids = children[id];
            var under = kids.Count == 0 ? 0 : kids.Sum(Span) + gap * (kids.Count - 1);
            return span[id] = Math.Max(top[id].Width, under);
        }
        var at = new Dictionary<string, Point>(StringComparer.Ordinal);
        void Lay(string id, double left)
        {
            var width = Span(id);
            var l = layers.GetValueOrDefault(id);
            at[id] = new Point(left + (width - top[id].Width) / 2, levelY[l] + (levelHeight[l] - top[id].Height) / 2);
            var kids = children[id];
            var under = kids.Count == 0 ? 0 : kids.Sum(Span) + gap * (kids.Count - 1);
            var x = left + (width - under) / 2;
            foreach (var kid in kids)
            {
                Lay(kid, x);
                x += Span(kid) + gap;
            }
        }
        var cursor = 0.0;
        foreach (var root in order.Where(id => !hasParent.Contains(id)))
        {
            Lay(root, cursor);
            cursor += Span(root) + 2 * gap;
        }
        return at;
    }

    /// <summary>
    /// The tree's children per step: a step hangs under the step it waits for that runs last (ties: the first in run
    /// order); steps waiting for nothing (or only in a cycle) are roots.
    /// </summary>
    private static Dictionary<string, List<string>> Children(List<string> order,
        List<(string From, string To, EdgeWhen When)> edges, Dictionary<string, int> layers)
    {
        var rank = order.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i, StringComparer.Ordinal);
        var children = order.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var id in order)
        {
            var from = edges.Where(e => e.To == id && layers.GetValueOrDefault(e.From) < layers.GetValueOrDefault(id))
                .Select(e => e.From).OrderByDescending(f => layers.GetValueOrDefault(f)).ThenBy(f => rank[f]).FirstOrDefault();
            if (from is not null)
                children[from].Add(id);
        }
        return children;
    }

    /// <summary>Each root, then its branches one after another depth first: a chain stays together round the ring.</summary>
    private static List<string> Preorder(List<string> order, Dictionary<string, List<string>> children)
    {
        var hasParent = children.Values.SelectMany(c => c).ToHashSet(StringComparer.Ordinal);
        var result = new List<string>(order.Count);
        void Walk(string id)
        {
            result.Add(id);
            foreach (var kid in children[id])
                Walk(kid);
        }
        foreach (var root in order.Where(id => !hasParent.Contains(id)))
            Walk(root);
        return result;
    }

    /// <summary>A ring in tree order (<see cref="Preorder"/>), clockwise from the top; each step's share of the ring is its width plus a gap.</summary>
    private static Dictionary<string, Point> Circle(List<string> order, Dictionary<string, Rect> top, LayoutOptions o)
    {
        var at = new Dictionary<string, Point>(StringComparer.Ordinal);
        if (order.Count == 1)
        {
            at[order[0]] = default;
            return at;
        }
        var gap = 3 * SiblingGap(o); // round the bottom, neighbours sit side by side and a line needs room between them
        var shares = order.Select(id => top[id].Width + gap).ToList();
        var total = shares.Sum();
        var radius = Math.Max(total / (2 * Math.PI), order.Max(id => top[id].Width) * 0.75);
        var angle = -Math.PI / 2;
        for (var i = 0; i < order.Count; i++)
        {
            var r = top[order[i]];
            at[order[i]] = new Point(radius * Math.Cos(angle) - r.Width / 2, radius * Math.Sin(angle) - r.Height / 2);
            angle += 2 * Math.PI * shares[i] / total;
        }
        return at;
    }

    /// <summary>
    /// Hand placement: placed ids keep their place; the rest, in run order, go one gap past the step they run after
    /// (right of it, or under it in the vertical shapes), or stay where the shape put them, then move down clear of
    /// everything already placed.
    /// </summary>
    private static Dictionary<string, Point> Place(List<string> order, Dictionary<string, Rect> top,
        List<(string From, string To, EdgeWhen When)> edges, Dictionary<string, int> layers,
        Dictionary<string, Point> shaped, IReadOnlyDictionary<string, Point> placed, LayoutOptions o)
    {
        var at = new Dictionary<string, Point>(StringComparer.Ordinal);
        foreach (var id in order)
        {
            if (placed.TryGetValue(id, out var p))
                at[id] = p;
        }
        if (at.Count == 0)
            return shaped;
        var vertical = o.Shape is LayoutShape.TopToBottom or LayoutShape.Tree;
        Rect Box(string id) => new(at[id].X, at[id].Y, top[id].Width, top[id].Height);
        foreach (var id in order.Where(id => !at.ContainsKey(id)))
        {
            var after = edges.Where(e => e.To == id && at.ContainsKey(e.From))
                .Select(e => e.From).OrderByDescending(f => layers.GetValueOrDefault(f)).FirstOrDefault();
            var spot = shaped[id];
            if (after is not null)
            {
                var a = Box(after);
                spot = vertical ? new Point(a.X, a.Bottom + LevelGap(o)) : new Point(a.Right + o.ColumnGap, a.Y);
            }
            var size = top[id];
            for (var tries = 0; tries <= at.Count; tries++)
            {
                var me = new Rect(spot.X, spot.Y, size.Width, size.Height);
                var hit = at.Keys.Select(Box).Where(r => Overlap(r, me, o.RowGap)).ToList();
                if (hit.Count == 0)
                    break;
                spot = new Point(spot.X, hit.Max(r => r.Bottom) + Math.Max(o.RowGap, Grid));
            }
            at[id] = new Point(Snap(spot.X), Snap(spot.Y));
        }
        return at;
    }

    private static bool Overlap(Rect a, Rect b, double gap) =>
        a.X < b.Right + gap && b.X < a.Right + gap && a.Y < b.Bottom + gap && b.Y < a.Bottom + gap;

    /// <summary>Moves everything so the top-left box sits at the padding (the shapes compute round their own origin).</summary>
    private static Dictionary<string, Point> Pad(Dictionary<string, Point> at, double padding)
    {
        if (at.Count == 0)
            return at;
        var dx = padding - at.Values.Min(p => p.X);
        var dy = padding - at.Values.Min(p => p.Y);
        if (dx <= 0 && dy <= 0)
            return at; // hand placement may leave room on the left or top: keep it
        dx = Math.Max(dx, 0);
        dy = Math.Max(dy, 0);
        return at.ToDictionary(kv => kv.Key, kv => new Point(kv.Value.X + dx, kv.Value.Y + dy), StringComparer.Ordinal);
    }

    /// <summary>Space between neighbours in a row, a tree level or round the ring.</summary>
    private static double SiblingGap(LayoutOptions o) => Math.Max(24, o.RowGap * 2);

    /// <summary>Space between rows or tree levels: room for the line and its arrowhead.</summary>
    private static double LevelGap(LayoutOptions o) => Math.Max(40, o.ColumnGap * 0.75);
}
