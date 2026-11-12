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
        var placed = HasPlaced(flow, o.Placed) ? o.Placed : null;
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
        var routes = edges.Select(e => Route(e.From, e.To, e.When, final, o.Shape, o.Lines)).ToList();

        // A lane round the outside may run left of or above everything: make room for it.
        var dx = Math.Max(0, o.Padding / 2 - routes.Where(r => r.Lane is not null && IsVertical(r.StartSide)).Select(r => r.Lane!.Value).DefaultIfEmpty(double.MaxValue).Min());
        var dy = Math.Max(0, o.Padding / 2 - routes.Where(r => r.Lane is not null && !IsVertical(r.StartSide)).Select(r => r.Lane!.Value).DefaultIfEmpty(double.MaxValue).Min());
        if (dx > 0 || dy > 0)
        {
            boxes = [.. boxes.Select(b => b with { Bounds = b.Bounds with { X = b.Bounds.X + dx, Y = b.Bounds.Y + dy } })];
            routes = [.. routes.Select(r => r with
            {
                Start = new Point(r.Start.X + dx, r.Start.Y + dy),
                End = new Point(r.End.X + dx, r.End.Y + dy),
                Lane = r.Lane + (IsVertical(r.StartSide) ? dx : dy),
            })];
        }
        var right = boxes.Count == 0 ? o.Padding : boxes.Max(b => b.Bounds.Right);
        var bottom = boxes.Count == 0 ? o.Padding : boxes.Max(b => b.Bounds.Bottom);
        foreach (var lane in routes.Where(r => r.Lane is not null))
        {
            if (IsVertical(lane.StartSide))
                right = Math.Max(right, lane.Lane!.Value);
            else
                bottom = Math.Max(bottom, lane.Lane!.Value);
        }
        return new FlowArrangement(boxes, routes, auto.Columns, new Size(right + o.Padding, bottom + o.Padding));
    }

    private const double LaneClearance = 14;

    private static bool IsVertical(PortSide side) => side is PortSide.Top or PortSide.Bottom;

    /// <summary>
    /// A line between two top-level boxes of <paramref name="boxes"/> (every top-level step where it is shown): by
    /// <see cref="Sides"/> and <see cref="Port"/>, detouring round any step it would be drawn through (D-124). The
    /// commit and the drag preview both route with it.
    /// </summary>
    public static EdgeRoute Route(string from, string to, EdgeWhen when, IReadOnlyDictionary<string, Rect> boxes, LayoutShape shape, LayoutLines lines)
    {
        ArgumentNullException.ThrowIfNull(boxes);
        var (a, b) = (boxes[from], boxes[to]);
        var (startSide, endSide) = Sides(a, b, shape);
        return Clear(new EdgeRoute(from, to, when, Port(a, startSide), Port(b, endSide), null, startSide, endSide), boxes, lines);
    }

    /// <summary>How many steps other than its own two the line is drawn through (sampled).</summary>
    private static int Hits(EdgeRoute route, IReadOnlyDictionary<string, Rect> boxes, LayoutLines lines)
    {
        var curve = EdgeCurve.For(route, lines);
        var points = Enumerable.Range(1, 127).Select(i => curve.At(i / 128.0)).ToList();
        return boxes.Count(kv => kv.Key != route.From && kv.Key != route.To
            && points.Any(p => p.X > kv.Value.X && p.X < kv.Value.Right && p.Y > kv.Value.Y && p.Y < kv.Value.Bottom));
    }

    /// <summary>
    /// A line drawn through a step detours (D-124): out of its port, along a lane just past every step beside its run
    /// (the nearer side; right or below on a tie), and back in. The detour is kept only if it crosses fewer steps.
    /// </summary>
    private static EdgeRoute Clear(EdgeRoute route, IReadOnlyDictionary<string, Rect> boxes, LayoutLines lines)
    {
        var hits = Hits(route, boxes, lines);
        if (hits == 0)
            return route;
        var shape = EdgeCurve.Detour(route.Start, route.End, route.StartSide, route.EndSide, 0);
        var (c1, c2, alongX) = (shape.Control1, shape.Control2, shape.LaneIsX);
        double Across(Point p) => alongX ? p.X : p.Y;
        double Along(Point p) => alongX ? p.Y : p.X;
        var (lo, hi) = (Math.Min(Along(c1), Along(c2)), Math.Max(Along(c1), Along(c2)));
        var band = boxes.Where(kv => kv.Key != route.From && kv.Key != route.To).Select(kv => kv.Value)
            .Where(r => alongX ? r.Bottom > lo && r.Y < hi : r.Right > lo && r.X < hi).ToList();
        var (near, far) = (Math.Min(Across(c1), Across(c2)), Math.Max(Across(c1), Across(c2)));
        var before = band.Select(r => alongX ? r.X : r.Y).Append(near).Min() - LaneClearance;
        var after = band.Select(r => alongX ? r.Right : r.Bottom).Append(far).Max() + LaneClearance;
        var mid = (near + far) / 2;
        var best = route;
        foreach (var lane in mid - before < after - mid ? [before, after] : new[] { after, before })
        {
            var tried = route with { Lane = lane };
            var count = Hits(tried, boxes, lines);
            if (count < hits)
                (best, hits) = (tried, count);
            if (hits == 0)
                break;
        }
        return best;
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
    /// A <b>side join</b> (D-124) is a step that also waits for an ancestor of the step it hangs under (a line that
    /// would skip a level and run through the steps between): it goes to the right of that ancestor's branch instead,
    /// centred up and down between the first and the last step it waits for, so every line comes in from the side.
    /// Joins are tried one at a time in run order; one whose side place doesn't cut the lines drawn through steps
    /// stays hung under.
    /// </summary>
    private static Dictionary<string, Point> Tree(List<string> order, Dictionary<string, Rect> top,
        List<(string From, string To, EdgeWhen When)> edges, Dictionary<string, int> layers, LayoutOptions o)
    {
        var children = Children(order, edges, layers);
        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (p, kids) in children)
        {
            foreach (var kid in kids)
                parent[kid] = p;
        }
        bool IsAncestor(string a, string of)
        {
            for (var p = of; parent.TryGetValue(p, out var up); p = up)
            {
                if (up == a)
                    return true;
            }
            return false;
        }

        var rank = order.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i, StringComparer.Ordinal);
        var side = new Dictionary<string, (string First, string Last)>(StringComparer.Ordinal);
        var at = TreeAt(order, top, children, side, layers, o);
        var crossings = Crossings(at, top, edges, o);
        foreach (var id in order)
        {
            if (crossings == 0 || !parent.TryGetValue(id, out var last))
                continue;
            var sources = edges.Where(e => e.To == id && e.From != last).Select(e => e.From).Distinct(StringComparer.Ordinal).ToList();
            if (sources.Count == 0 || !sources.All(s => IsAncestor(s, last)))
                continue;
            var first = sources.OrderBy(s => layers.GetValueOrDefault(s)).ThenBy(s => rank[s]).First();
            side[id] = (first, last);
            var tried = TreeAt(order, top, children, side, layers, o);
            var count = Crossings(tried, top, edges, o);
            if (count < crossings)
                (at, crossings) = (tried, count);
            else
                side.Remove(id);
        }
        return at;
    }

    /// <summary>The tidy tree for a given set of side joins (<see cref="Tree"/>).</summary>
    private static Dictionary<string, Point> TreeAt(List<string> order, Dictionary<string, Rect> top,
        Dictionary<string, List<string>> hung, Dictionary<string, (string First, string Last)> side,
        Dictionary<string, int> layers, LayoutOptions o)
    {
        // A side join leaves the step it hung under and goes beside its first source's branch, in run order.
        var children = hung.ToDictionary(kv => kv.Key, kv => kv.Value.Where(k => !side.ContainsKey(k)).ToList(), StringComparer.Ordinal);
        var sides = order.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var id in order.Where(side.ContainsKey))
            sides[side[id].First].Add(id);
        var hasParent = hung.Values.SelectMany(c => c).ToHashSet(StringComparer.Ordinal);

        var levels = order.Select(id => layers.GetValueOrDefault(id)).DefaultIfEmpty(0).Max() + 1;
        var levelHeight = new double[levels];
        foreach (var id in order)
            levelHeight[layers.GetValueOrDefault(id)] = Math.Max(levelHeight[layers.GetValueOrDefault(id)], top[id].Height);
        var levelY = new double[levels];
        for (var l = 1; l < levels; l++)
            levelY[l] = levelY[l - 1] + levelHeight[l - 1] + LevelGap(o);

        var gap = SiblingGap(o);
        var core = new Dictionary<string, double>(StringComparer.Ordinal);
        var span = new Dictionary<string, double>(StringComparer.Ordinal);
        double Under(string id)
        {
            var kids = children[id];
            return kids.Count == 0 ? 0 : kids.Sum(Span) + gap * (kids.Count - 1);
        }
        // Core: the step over its own children; its span adds its side joins to the right.
        double Core(string id) => core.TryGetValue(id, out var known) ? known : core[id] = Math.Max(top[id].Width, Under(id));
        double Span(string id) => span.TryGetValue(id, out var known) ? known : span[id] = Core(id) + sides[id].Sum(s => gap + Span(s));

        var at = new Dictionary<string, Point>(StringComparer.Ordinal);
        void Lay(string id, double left)
        {
            var width = Core(id);
            var l = layers.GetValueOrDefault(id);
            at[id] = new Point(left + (width - top[id].Width) / 2, levelY[l] + (levelHeight[l] - top[id].Height) / 2);
            var x = left + (width - Under(id)) / 2;
            foreach (var kid in children[id])
            {
                Lay(kid, x);
                x += Span(kid) + gap;
            }
            x = left + width + gap;
            foreach (var join in sides[id])
            {
                Lay(join, x);
                x += Span(join) + gap;
            }
        }
        var cursor = 0.0;
        foreach (var root in order.Where(id => !hasParent.Contains(id)))
        {
            Lay(root, cursor);
            cursor += Span(root) + 2 * gap;
        }
        // Side joins move up between the first and the last step they wait for; their own branch moves with them.
        foreach (var id in order.Where(side.ContainsKey))
        {
            var (first, last) = side[id];
            var centre = (at[first].Y + top[first].Height / 2 + at[last].Y + top[last].Height / 2) / 2;
            var dy = centre - top[id].Height / 2 - at[id].Y;
            void Shift(string s)
            {
                at[s] = new Point(at[s].X, at[s].Y + dy);
                foreach (var k in children[s].Concat(sides[s]))
                    Shift(k);
            }
            Shift(id);
        }
        return at;
    }

    /// <summary>
    /// How many (line, step) pairs cross in the tree: a line drawn (Curved or Right angles) through a step that is
    /// neither of its ends.
    /// </summary>
    private static int Crossings(Dictionary<string, Point> at, Dictionary<string, Rect> top,
        List<(string From, string To, EdgeWhen When)> edges, LayoutOptions o)
    {
        var boxes = at.ToDictionary(kv => kv.Key, kv => new Rect(kv.Value.X, kv.Value.Y, top[kv.Key].Width, top[kv.Key].Height), StringComparer.Ordinal);
        var count = 0;
        foreach (var (from, to, when) in edges)
        {
            var (a, b) = (boxes[from], boxes[to]);
            var (s, e) = Sides(a, b, LayoutShape.Tree);
            var curve = EdgeCurve.For(new EdgeRoute(from, to, when, Port(a, s), Port(b, e), null, s, e), o.Lines);
            var points = Enumerable.Range(1, 63).Select(i => curve.At(i / 64.0)).ToList();
            count += boxes.Count(kv => kv.Key != from && kv.Key != to
                && points.Any(p => p.X > kv.Value.X && p.X < kv.Value.Right && p.Y > kv.Value.Y && p.Y < kv.Value.Bottom));
        }
        return count;
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
        if (order.Count == 0)
            return at;
        if (order.Count == 1)
        {
            at[order[0]] = default;
            return at;
        }
        var gap = 3 * SiblingGap(o); // round the bottom, neighbours sit side by side and a line needs room between them
        // A box's share is its larger side: on the ring's sides neighbours sit above each other, so a tall card needs its height.
        var shares = order.Select(id => Math.Max(top[id].Width, top[id].Height) + gap).ToList();
        var total = shares.Sum();
        var radius = Math.Max(total / (2 * Math.PI), order.Max(id => Math.Max(top[id].Width, top[id].Height)) * 0.75);
        // Each box sits in the middle of its share, the first one on top. A chord is shorter than its arc, so a ring of
        // a few boxes (or very uneven ones) can still overlap: then it grows until nothing does.
        for (var tries = 0; tries < 40; tries++)
        {
            var angle = -Math.PI / 2 - Math.PI * shares[0] / total;
            for (var i = 0; i < order.Count; i++)
            {
                var r = top[order[i]];
                var middle = angle + Math.PI * shares[i] / total;
                at[order[i]] = new Point(radius * Math.Cos(middle) - r.Width / 2, radius * Math.Sin(middle) - r.Height / 2);
                angle += 2 * Math.PI * shares[i] / total;
            }
            var boxes = order.Select(id => new Rect(at[id].X, at[id].Y, top[id].Width, top[id].Height)).ToList();
            var clear = true;
            for (var i = 0; i < boxes.Count && clear; i++)
            {
                for (var j = i + 1; j < boxes.Count && clear; j++)
                    clear = !Overlap(boxes[i], boxes[j], o.RowGap);
            }
            if (clear)
                break;
            radius *= 1.15;
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

    /// <summary>
    /// Whether any hand-placed id is still a top-level step or card of <paramref name="flow"/>: ids left behind by
    /// deleted steps don't count, so a flow whose placed steps are all gone is laid out by its shape again.
    /// </summary>
    public static bool HasPlaced(Flow flow, IReadOnlyDictionary<string, Point>? placed)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (placed is not { Count: > 0 })
            return false;
        var members = FlowLayout.CardOf(flow);
        return flow.Nodes.Any(n => !members.ContainsKey(n.Id) && placed.ContainsKey(n.Id));
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
