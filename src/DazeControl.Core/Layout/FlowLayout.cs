using DazeControl.Core.Model;

namespace DazeControl.Core.Layout;

public readonly record struct Point(double X, double Y);

public readonly record struct Size(double Width, double Height);

public readonly record struct Rect(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;

    public double Bottom => Y + Height;

    public double CenterY => Y + Height / 2;

    public bool Contains(Point p) => p.X >= X && p.X <= Right && p.Y >= Y && p.Y <= Bottom;

    public bool Intersects(Rect other) => X <= other.Right && other.X <= Right && Y <= other.Bottom && other.Y <= Bottom;

    /// <summary>The rectangle spanned by two corners in any order (a marquee).</summary>
    public static Rect FromCorners(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}

/// <summary>Layout menu › Spacing (D-067): the gaps between columns and between boxes in a column.</summary>
public enum LayoutSpacing
{
    Compact,
    Normal,
    Roomy,
}

/// <summary>Layout menu › Line up (D-067): a column's boxes centred on its line, or hung from the line's top.</summary>
public enum LayoutAlign
{
    Centre,
    Top,
}

/// <summary>Layout menu › Lines (D-067): Bézier curves, or every line orthogonal with rounded corners.</summary>
public enum LayoutLines
{
    Curved,
    RightAngles,
}

/// <summary>Layout menu › Arrange (D-115): where the automatic layout puts the boxes nobody placed by hand.</summary>
public enum LayoutShape
{
    /// <summary>One column per layer, each centred on its line; long chains wrap (D-060).</summary>
    Columns,

    /// <summary>Columns too, but every box sits beside the step it waits for, branches fanning out from it.</summary>
    Tree,

    /// <summary>All boxes on one ring in run order, clockwise from the left.</summary>
    Circle,
}

/// <summary>The side of a box an edge leaves from or enters at.</summary>
public enum PortSide
{
    Right,
    Left,
    Top,
    Bottom,
}

/// <summary>Spacing in DIP (UX §5.2, §5.3) and the Layout menu's choices (D-067).</summary>
public sealed record LayoutOptions
{
    public static LayoutOptions Default { get; } = new();

    /// <summary>The default for "Wrap long chains" (After 5 steps).</summary>
    public const int DefaultWrapAfter = 5;

    public double ColumnGap { get; init; } = 64;

    public double RowGap { get; init; } = 16;

    public double Padding { get; init; } = 48;

    public double CardPadding { get; init; } = 12;

    public double CardHeader { get; init; } = 32;

    public double MemberSpacing { get; init; } = 8;

    /// <summary>A spine run longer than this many boxes wraps onto rows of this many (D-060); 0 = never wrap.</summary>
    public int WrapAfter { get; init; } = DefaultWrapAfter;

    public double WrapRowGap { get; init; } = 48;

    public LayoutAlign Align { get; init; } = LayoutAlign.Centre;

    public LayoutLines Lines { get; init; } = LayoutLines.Curved;

    public LayoutShape Shape { get; init; } = LayoutShape.Columns;

    /// <summary>
    /// Top-left corners of the top-level boxes the user dragged somewhere (D-115); everything else is laid out as
    /// if they weren't. Null = none (so two default options stay equal).
    /// </summary>
    public IReadOnlyDictionary<string, Point>? Pins { get; init; }

    /// <summary>Column and row gap for a Spacing choice: Compact 40/8, Normal 64/16, Roomy 96/32 (D-067).</summary>
    public static (double Column, double Row) Gaps(LayoutSpacing spacing) => spacing switch
    {
        LayoutSpacing.Compact => (40, 8),
        LayoutSpacing.Roomy => (96, 32),
        _ => (64, 16),
    };

    /// <summary>The options for the Layout menu's choices; a negative <paramref name="wrapAfter"/> counts as never.</summary>
    public static LayoutOptions Of(int wrapAfter, LayoutSpacing spacing, LayoutAlign align, LayoutLines lines)
    {
        var (column, row) = Gaps(spacing);
        return Default with { WrapAfter = Math.Max(0, wrapAfter), ColumnGap = column, RowGap = row, Align = align, Lines = lines };
    }
}

/// <param name="Card">The card a member sits in; null for a top-level step or a card.</param>
/// <param name="Column">The column of the node (a member: its card's).</param>
/// <param name="Row">Index within the column (a member: within its card).</param>
public sealed record NodeBox(string Id, Rect Bounds, int Column, int Row, string? Card);

/// <summary>An edge between two top-level boxes; an edge naming a card member is drawn from/to its card.</summary>
/// <param name="WrapLaneY">Set on an edge from a wrapped row's end to the next row's start (D-060), or from a block's
/// last box to the line under it (D-066): the gap's middle; or on a long edge that detours round the boxes it skips (D-111): the lane.</param>
/// <param name="Leaves">The side of the source the edge starts on: the right, except between boxes placed by hand
/// or on a ring (D-115), where it is the side facing the target.</param>
/// <param name="Enters">The side of the target the arrow points into.</param>
/// <param name="IsFloating">Between boxes placed by hand or on a ring: always a curve between the two facing sides,
/// also when those happen to be right → left.</param>
public sealed record EdgeRoute(string From, string To, EdgeWhen When, Point Start, Point End, double? WrapLaneY = null,
    PortSide Leaves = PortSide.Right, PortSide Enters = PortSide.Left, bool IsFloating = false);

/// <param name="Columns">Top-level ids per column, top to bottom.</param>
public sealed record FlowArrangement(
    IReadOnlyList<NodeBox> Boxes,
    IReadOnlyList<EdgeRoute> Edges,
    IReadOnlyList<IReadOnlyList<string>> Columns,
    Size Extent)
{
    public NodeBox? Find(string id) => Boxes.FirstOrDefault(b => b.Id == id);
}

/// <summary>
/// The canvas layout, automatic (D-020) except for boxes the user placed by hand and the Arrange choice (D-115,
/// <see cref="LayoutOptions.Pins"/>, <see cref="LayoutOptions.Shape"/>). Columns: one column per layer left→right, a node's layer = 1 + the
/// highest layer it waits for, order within a column = order in <see cref="Flow.Nodes"/>. Columns are centred on
/// the tallest of their line (or hung from its top, <see cref="LayoutOptions.Align"/>); long simple chains wrap onto rows (D-060) and what follows starts a new line under the
/// block (D-066). Works on invalid flows too (autosave keeps half-built ones): cycles and unknown ids are ignored.
/// </summary>
public static class FlowLayout
{
    /// <summary>The first node with an id wins, as in validation; later duplicates are ignored.</summary>
    private static Flow Unique(Flow flow) =>
        flow.Nodes.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count() == flow.Nodes.Count
            ? flow
            : flow with { Nodes = [.. flow.Nodes.DistinctBy(n => n.Id, StringComparer.Ordinal)] };

    /// <summary>Card id for each member id (the first card listing a step wins, as in validation).</summary>
    public static Dictionary<string, string> CardOf(Flow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        flow = Unique(flow);
        var steps = flow.Nodes.OfType<StepNode>().Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        var cardOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var card in flow.Nodes.OfType<GroupNode>())
        {
            foreach (var member in card.Members)
            {
                if (steps.Contains(member))
                    cardOf.TryAdd(member, card.Id);
            }
        }
        return cardOf;
    }

    /// <summary>Top-level edges (member ends mapped to their card; self, duplicate and dangling edges dropped).</summary>
    public static IReadOnlyList<(string From, string To, EdgeWhen When)> Edges(Flow flow, IReadOnlyDictionary<string, string> cardOf)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(cardOf);
        flow = Unique(flow);
        var ids = flow.Nodes.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<(string, string)>();
        var edges = new List<(string, string, EdgeWhen)>();
        foreach (var node in flow.Nodes)
        {
            var to = cardOf.GetValueOrDefault(node.Id, node.Id);
            foreach (var edge in node.After)
            {
                if (!ids.Contains(edge.From))
                    continue;
                var from = cardOf.GetValueOrDefault(edge.From, edge.From);
                if (from != to && seen.Add((from, to)))
                    edges.Add((from, to, edge.When));
            }
        }
        return edges;
    }

    /// <summary>Layer per top-level node.</summary>
    public static Dictionary<string, int> Layers(Flow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        flow = Unique(flow);
        var cardOf = CardOf(flow);
        return Layers(flow, cardOf, Edges(flow, cardOf));
    }

    private static Dictionary<string, int> Layers(Flow flow, Dictionary<string, string> cardOf, IReadOnlyList<(string From, string To, EdgeWhen When)> edges)
    {
        var top = flow.Nodes.Where(n => !cardOf.ContainsKey(n.Id)).Select(n => n.Id).Distinct(StringComparer.Ordinal).ToList();
        var incoming = top.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
        foreach (var (from, to, _) in edges)
        {
            if (incoming.TryGetValue(to, out var list) && incoming.ContainsKey(from))
                list.Add(from);
        }
        var layers = new Dictionary<string, int>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        int Visit(string id)
        {
            if (layers.TryGetValue(id, out var known))
                return known;
            if (!visiting.Add(id))
                return -1; // a cycle: this edge doesn't push the layer
            var layer = 0;
            foreach (var from in incoming[id])
                layer = Math.Max(layer, Visit(from) + 1);
            visiting.Remove(id);
            layers[id] = layer;
            return layer;
        }
        foreach (var id in top)
            Visit(id);
        return layers;
    }

    /// <param name="measure">Desired size of a step's pill (a member's height counts; its width stretches to the card).</param>
    public static FlowArrangement Arrange(Flow flow, Func<string, Size> measure, LayoutOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(measure);
        var o = options ?? LayoutOptions.Default;
        flow = Unique(flow);
        var cardOf = CardOf(flow);
        var edges = Edges(flow, cardOf);
        var layers = Layers(flow, cardOf, edges);

        var membersOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var card in flow.Nodes.OfType<GroupNode>())
            membersOf[card.Id] = [.. card.Members.Distinct(StringComparer.Ordinal).Where(m => cardOf.GetValueOrDefault(m) == card.Id)];

        var sizes = new Dictionary<string, Size>(StringComparer.Ordinal);
        foreach (var id in layers.Keys)
        {
            if (!membersOf.TryGetValue(id, out var members))
            {
                sizes[id] = measure(id);
                continue;
            }
            var inner = members.Select(measure).ToList();
            var width = inner.Count == 0 ? measure(id).Width : inner.Max(s => s.Width);
            var height = inner.Sum(s => s.Height) + o.MemberSpacing * Math.Max(0, inner.Count - 1);
            sizes[id] = new Size(width + 2 * o.CardPadding, o.CardHeader + height + 2 * o.CardPadding);
        }

        var columnCount = layers.Count == 0 ? 0 : layers.Values.Max() + 1;
        var columns = Enumerable.Range(0, columnCount).Select(_ => new List<string>()).ToList();
        foreach (var node in flow.Nodes)
        {
            if (layers.TryGetValue(node.Id, out var layer) && !columns[layer].Contains(node.Id))
                columns[layer].Add(node.Id);
        }
        var heights = columns.Select(c => c.Sum(id => sizes[id].Height) + o.RowGap * Math.Max(0, c.Count - 1)).ToList();
        var runs = SpineRuns(columns, edges, layers, o.WrapAfter);

        var boxes = new List<NodeBox>();
        void Place(string id, double x, double y, int column, int row)
        {
            var size = sizes[id];
            boxes.Add(new NodeBox(id, new Rect(x, y, size.Width, size.Height), column, row, null));
            if (!membersOf.TryGetValue(id, out var members))
                return;
            var my = y + o.CardHeader + o.CardPadding;
            for (var m = 0; m < members.Count; m++)
            {
                var h = measure(members[m]).Height;
                boxes.Add(new NodeBox(members[m], new Rect(x + o.CardPadding, my, size.Width - 2 * o.CardPadding, h), column, m, id));
                my += h + o.MemberSpacing;
            }
        }

        // Lines, read like text (D-066): a line is the columns up to and including the next wrapped block; the line
        // after a block starts at the block's x, one WrapRowGap under it, and its columns are centred among themselves.
        var wrapLanes = new Dictionary<(string, string), double>();
        var spots = o.Shape switch
        {
            LayoutShape.Tree => TreeSpots(columns, edges, layers, sizes, o),
            LayoutShape.Circle => CircleSpots(columns, sizes, o),
            _ => null,
        };
        if (spots is not null)
        {
            for (var l = 0; l < columns.Count; l++)
            {
                for (var r = 0; r < columns[l].Count; r++)
                    Place(columns[l][r], spots[columns[l][r]].X, spots[columns[l][r]].Y, l, r);
            }
        }
        double Offset(double room, double height) => o.Align == LayoutAlign.Top ? 0 : (room - height) / 2;
        var lineX = o.Padding;
        var lineTop = o.Padding;
        for (var c = 0; spots is null && c < columns.Count;)
        {
            var end = c;
            while (end < columns.Count && !runs.ContainsKey(end))
                end++;
            var length = end < columns.Count ? runs[end] : 0;
            List<string[]> rows = length == 0 ? [] : Enumerable.Range(end, length).Select(l => columns[l][0]).Chunk(o.WrapAfter).ToList();
            var rowHeights = rows.Select(row => row.Max(id => sizes[id].Height)).ToList();
            var tallest = Enumerable.Range(c, end - c).Select(l => heights[l]).Concat(rowHeights.Take(1)).DefaultIfEmpty(0).Max();

            var cursor = lineX;
            for (; c < end; c++)
            {
                var y = lineTop + Offset(tallest, heights[c]);
                for (var r = 0; r < columns[c].Count; r++)
                {
                    Place(columns[c][r], cursor, y, c, r);
                    y += sizes[columns[c][r]].Height + o.RowGap;
                }
                cursor += (columns[c].Count == 0 ? 0 : columns[c].Max(id => sizes[id].Width)) + o.ColumnGap;
            }
            if (length == 0)
                break;

            // A spine run: rows of WrapAfter boxes like text, slots aligned across rows (D-060).
            var slotWidths = Enumerable.Range(0, o.WrapAfter)
                .Select(k => rows.Where(row => k < row.Length).Max(row => sizes[row[k]].Width)).ToList();
            var rowTop = lineTop + Offset(tallest, rowHeights[0]);
            for (var r = 0; r < rows.Count; r++)
            {
                var x = cursor;
                for (var k = 0; k < rows[r].Length; k++)
                {
                    Place(rows[r][k], x, rowTop + Offset(rowHeights[r], sizes[rows[r][k]].Height), c + r * o.WrapAfter + k, 0);
                    x += slotWidths[k] + o.ColumnGap;
                }
                var lane = rowTop + rowHeights[r] + o.WrapRowGap / 2;
                if (r + 1 < rows.Count)
                    wrapLanes[(rows[r][^1], rows[r + 1][0])] = lane;
                else if (c + length < columns.Count)
                {
                    // Into the next line: every box in the column after the run hangs off the run's last box.
                    foreach (var next in columns[c + length])
                        wrapLanes[(rows[r][^1], next)] = lane;
                }
                rowTop += rowHeights[r] + o.WrapRowGap;
            }
            lineX = cursor;
            lineTop = rowTop;
            c += length;
        }

        // Boxes placed by hand (D-115): moved, with their members, after everything else took its automatic place.
        var pinned = new HashSet<string>(StringComparer.Ordinal);
        if (o.Pins is { Count: > 0 } pins)
        {
            var laidOut = boxes.Where(b => b.Card is null).ToDictionary(b => b.Id, b => b.Bounds, StringComparer.Ordinal);
            for (var i = 0; i < boxes.Count; i++)
            {
                var top = boxes[i].Card ?? boxes[i].Id;
                if (!pins.TryGetValue(top, out var pin))
                    continue;
                var r = boxes[i].Bounds;
                boxes[i] = boxes[i] with { Bounds = r with { X = r.X + Math.Max(0, pin.X) - laidOut[top].X, Y = r.Y + Math.Max(0, pin.Y) - laidOut[top].Y } };
                pinned.Add(top);
            }
        }

        var byId = boxes.Where(b => b.Card is null).ToDictionary(b => b.Id, b => b.Bounds, StringComparer.Ordinal);
        var routes = new List<EdgeRoute>();
        var detourBottom = 0.0;
        foreach (var (from, to, when) in edges)
        {
            if (byId.TryGetValue(from, out var a) && byId.TryGetValue(to, out var b))
            {
                if (o.Shape == LayoutShape.Circle || pinned.Contains(from) || pinned.Contains(to))
                {
                    routes.Add(Floating(from, to, when, a, b));
                    continue;
                }
                var start = new Point(a.Right, a.CenterY);
                var end = new Point(b.X, b.CenterY);
                double? lane = wrapLanes.TryGetValue((from, to), out var wrapLane) ? wrapLane : DetourLane(byId, from, to, start, end, o);
                if (lane is { } y)
                    detourBottom = Math.Max(detourBottom, y);
                routes.Add(new EdgeRoute(from, to, when, start, end, lane));
            }
        }

        var right = boxes.Count == 0 ? o.Padding : boxes.Max(b => b.Bounds.Right);
        var bottom = Math.Max(boxes.Count == 0 ? o.Padding : boxes.Max(b => b.Bounds.Bottom), detourBottom);
        return new FlowArrangement(boxes, routes, columns, new Size(right + o.Padding, bottom + o.Padding));
    }

    /// <summary>
    /// An edge between the two sides that face each other (D-115): sideways when the boxes are further apart
    /// sideways than up and down, else from the bottom of one into the top of the other (or the reverse).
    /// </summary>
    private static EdgeRoute Floating(string from, string to, EdgeWhen when, Rect a, Rect b)
    {
        var gapX = Math.Max(b.X - a.Right, a.X - b.Right);
        var gapY = Math.Max(b.Y - a.Bottom, a.Y - b.Bottom);
        PortSide leaves, enters;
        if (gapX >= gapY)
            (leaves, enters) = b.X + b.Width / 2 >= a.X + a.Width / 2 ? (PortSide.Right, PortSide.Left) : (PortSide.Left, PortSide.Right);
        else
            (leaves, enters) = b.CenterY >= a.CenterY ? (PortSide.Bottom, PortSide.Top) : (PortSide.Top, PortSide.Bottom);
        return new EdgeRoute(from, to, when, PortAt(a, leaves), PortAt(b, enters), null, leaves, enters, IsFloating: true);
    }

    /// <summary>The middle of a box's side.</summary>
    public static Point PortAt(Rect box, PortSide side) => side switch
    {
        PortSide.Left => new(box.X, box.CenterY),
        PortSide.Top => new(box.X + box.Width / 2, box.Y),
        PortSide.Bottom => new(box.X + box.Width / 2, box.Bottom),
        _ => new(box.Right, box.CenterY),
    };

    /// <summary>
    /// Arrange › Tree (D-115): a box keeps its layer's column but sits in the band of the step it waits for (the
    /// deepest one; the first on a tie), which is centred on its branches. Roots stack in <c>nodes</c> order.
    /// </summary>
    private static Dictionary<string, Point> TreeSpots(
        List<List<string>> columns, IReadOnlyList<(string From, string To, EdgeWhen When)> edges, Dictionary<string, int> layers,
        Dictionary<string, Size> sizes, LayoutOptions o)
    {
        var xs = new double[columns.Count];
        var x = o.Padding;
        for (var c = 0; c < columns.Count; c++)
        {
            xs[c] = x;
            x += (columns[c].Count == 0 ? 0 : columns[c].Max(id => sizes[id].Width)) + o.ColumnGap;
        }

        var parent = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (from, to, _) in edges)
        {
            // Only edges that go a layer deeper: an edge of a cycle (a half-built flow) can't make a tree.
            if (layers.TryGetValue(from, out var a) && layers.TryGetValue(to, out var b) && a < b
                && (!parent.TryGetValue(to, out var known) || a > layers[known]))
                parent[to] = from;
        }
        var children = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var roots = new List<string>();
        foreach (var id in columns.SelectMany(c => c))
        {
            if (!parent.TryGetValue(id, out var p))
                roots.Add(id);
            else if (children.TryGetValue(p, out var list))
                list.Add(id);
            else
                children[p] = [id];
        }

        var spans = new Dictionary<string, double>(StringComparer.Ordinal);
        double Branches(string id) => children.TryGetValue(id, out var list) ? list.Sum(Span) + o.RowGap * (list.Count - 1) : 0;
        double Span(string id) => spans.TryGetValue(id, out var known) ? known : spans[id] = Math.Max(sizes[id].Height, Branches(id));

        var spots = new Dictionary<string, Point>(StringComparer.Ordinal);
        void Put(string id, double top)
        {
            var span = Span(id);
            spots[id] = new Point(xs[layers[id]], top + (span - sizes[id].Height) / 2);
            if (!children.TryGetValue(id, out var list))
                return;
            var y = top + (span - Branches(id)) / 2;
            foreach (var child in list)
            {
                Put(child, y);
                y += Span(child) + o.RowGap;
            }
            // Midway between its first and last branch (their bands may differ in height), not the band's middle.
            var middle = (spots[list[0]].Y + sizes[list[0]].Height / 2 + spots[list[^1]].Y + sizes[list[^1]].Height / 2) / 2;
            spots[id] = spots[id] with { Y = Math.Clamp(middle - sizes[id].Height / 2, top, top + span - sizes[id].Height) };
        }
        var rootTop = o.Padding;
        foreach (var root in roots)
        {
            Put(root, rootTop);
            rootTop += Span(root) + o.RowGap;
        }
        return spots;
    }

    /// <summary>
    /// Arrange › Circle (D-115): every top-level box on one ellipse in run order (by column, then row), the first on
    /// the left and the rest clockwise, so the upper half still reads left to right. The radii grow with the count:
    /// wide enough that neighbours at the top and bottom clear each other sideways, tall enough at the sides.
    /// </summary>
    private static Dictionary<string, Point> CircleSpots(List<List<string>> columns, Dictionary<string, Size> sizes, LayoutOptions o)
    {
        var ids = columns.SelectMany(c => c).ToList();
        var spots = new Dictionary<string, Point>(StringComparer.Ordinal);
        if (ids.Count == 0)
            return spots;
        var width = ids.Max(id => sizes[id].Width);
        var height = ids.Max(id => sizes[id].Height);
        if (ids.Count == 1)
        {
            spots[ids[0]] = new Point(o.Padding, o.Padding);
            return spots;
        }
        // √2: between the top and a side, neighbours are apart only partly sideways and partly up and down.
        var k = Math.Sqrt(2) * ids.Count / (2 * Math.PI);
        var rx = k * (width + o.ColumnGap);
        var ry = Math.Max(k * (height + o.RowGap), height + 2 * o.RowGap);
        var cx = o.Padding + rx + width / 2;
        var cy = o.Padding + ry + height / 2;
        for (var i = 0; i < ids.Count; i++)
        {
            var t = 2 * Math.PI * i / ids.Count;
            var size = sizes[ids[i]];
            spots[ids[i]] = new Point(cx - rx * Math.Cos(t) - size.Width / 2, cy - ry * Math.Sin(t) - size.Height / 2);
        }
        return spots;
    }

    private const double DetourClearance = 14;

    /// <summary>
    /// The lane an edge runs along to get round the boxes it would otherwise strike through (D-111), or null when its
    /// line is clear. The lane sits just above or below every box between the two ends, whichever is nearer.
    /// </summary>
    private static double? DetourLane(Dictionary<string, Rect> byId, string from, string to, Point start, Point end, LayoutOptions o)
    {
        if (end.X - start.X <= 2 * EdgeCurve.WrapStep)
            return null;
        var between = byId.Where(kv => kv.Key != from && kv.Key != to && kv.Value.X >= start.X && kv.Value.Right <= end.X)
            .Select(kv => kv.Value).ToList();
        if (between.Count == 0)
            return null;
        var curve = EdgeCurve.Between(start, end);
        var blocked = false;
        for (var i = 1; i < 32 && !blocked; i++)
        {
            var p = o.Lines == LayoutLines.RightAngles ? RightAngledPoint(start, end, i / 32.0) : curve.At(i / 32.0);
            blocked = between.Any(r => p.X >= r.X - 2 && p.X <= r.Right + 2 && p.Y >= r.Y - 2 && p.Y <= r.Bottom + 2);
        }
        if (!blocked)
            return null;
        var above = between.Min(r => r.Y) - DetourClearance;
        var below = between.Max(r => r.Bottom) + DetourClearance;
        var mid = (start.Y + end.Y) / 2;
        return mid - above <= below - mid ? above : below;
    }

    private static Point RightAngledPoint(Point start, Point tip, double t) => EdgeCurve.RightAngled(start, tip).At(t);

    /// <summary>
    /// Spine runs longer than <paramref name="wrapAfter"/> as first layer → length (D-060): consecutive layers of one
    /// box each, every box with at most one edge in and one out, and no other edge spanning the run's layers.
    /// </summary>
    private static Dictionary<int, int> SpineRuns(
        List<List<string>> columns, IReadOnlyList<(string From, string To, EdgeWhen When)> edges, Dictionary<string, int> layers, int wrapAfter)
    {
        var runs = new Dictionary<int, int>();
        if (wrapAfter < 1)
            return runs;
        var inCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var outCount = new Dictionary<string, int>(StringComparer.Ordinal);
        var crossed = new bool[columns.Count];
        foreach (var (from, to, _) in edges)
        {
            inCount[to] = inCount.GetValueOrDefault(to) + 1;
            outCount[from] = outCount.GetValueOrDefault(from) + 1;
            if (!layers.TryGetValue(from, out var a) || !layers.TryGetValue(to, out var b))
                continue;
            for (var l = Math.Min(a, b) + 1; l < Math.Max(a, b); l++)
                crossed[l] = true;
        }
        bool Eligible(int l) => columns[l].Count == 1 && !crossed[l]
            && inCount.GetValueOrDefault(columns[l][0]) <= 1 && outCount.GetValueOrDefault(columns[l][0]) <= 1;

        var start = -1;
        for (var l = 0; l <= columns.Count; l++)
        {
            // A box continues the run only if its one edge in comes from the previous box (a layer > 0 always has one).
            var continues = l < columns.Count && Eligible(l) && start >= 0
                && edges.Any(e => e.From == columns[l - 1][0] && e.To == columns[l][0]);
            if (continues)
                continue;
            if (start >= 0 && l - start > wrapAfter)
                runs[start] = l - start;
            start = l < columns.Count && Eligible(l) ? l : -1;
        }
        return runs;
    }
}
