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

/// <summary>Spacing in DIP (UX §5.2, §5.3).</summary>
public sealed record LayoutOptions
{
    public static LayoutOptions Default { get; } = new();

    public double ColumnGap { get; init; } = 64;

    public double RowGap { get; init; } = 16;

    public double Padding { get; init; } = 48;

    public double CardPadding { get; init; } = 12;

    public double CardHeader { get; init; } = 32;

    public double MemberSpacing { get; init; } = 8;

    /// <summary>A spine run longer than this many boxes wraps onto rows of this many (D-060).</summary>
    public int WrapAfter { get; init; } = 5;

    public double WrapRowGap { get; init; } = 48;
}

/// <param name="Card">The card a member sits in; null for a top-level step or a card.</param>
/// <param name="Column">The column of the node (a member: its card's).</param>
/// <param name="Row">Index within the column (a member: within its card).</param>
public sealed record NodeBox(string Id, Rect Bounds, int Column, int Row, string? Card);

/// <summary>An edge between two top-level boxes; an edge naming a card member is drawn from/to its card.</summary>
/// <param name="WrapLaneY">Set on an edge from a wrapped row's end to the next row's start: the gap's middle (D-060).</param>
public sealed record EdgeRoute(string From, string To, EdgeWhen When, Point Start, Point End, double? WrapLaneY = null);

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
/// The canvas layout, always automatic (D-020): one column per layer left→right, a node's layer = 1 + the
/// highest layer it waits for, order within a column = order in <see cref="Flow.Nodes"/>. Columns are centred on
/// the tallest; long simple chains wrap onto rows (D-060). Works on invalid flows too (autosave keeps half-built ones): cycles and unknown ids are ignored.
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
        var tallest = heights.Count == 0 ? 0 : heights.Max();
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

        var wrapLanes = new Dictionary<(string, string), double>();
        var cursor = o.Padding;
        var dy = 0.0; // after a wrapped block, what follows is top-aligned with its last row
        for (var c = 0; c < columns.Count; c++)
        {
            if (!runs.TryGetValue(c, out var length))
            {
                var y = o.Padding + (tallest - heights[c]) / 2 + dy;
                for (var r = 0; r < columns[c].Count; r++)
                {
                    Place(columns[c][r], cursor, y, c, r);
                    y += sizes[columns[c][r]].Height + o.RowGap;
                }
                cursor += (columns[c].Count == 0 ? 0 : columns[c].Max(id => sizes[id].Width)) + o.ColumnGap;
                continue;
            }

            // A spine run: rows of WrapAfter boxes like text, slots aligned across rows (D-060).
            var ids = Enumerable.Range(c, length).Select(l => columns[l][0]).ToList();
            var rows = ids.Chunk(o.WrapAfter).ToList();
            var slotWidths = Enumerable.Range(0, o.WrapAfter)
                .Select(k => rows.Where(row => k < row.Length).Max(row => sizes[row[k]].Width)).ToList();
            var rowTop = o.Padding + (tallest - sizes[ids[0]].Height) / 2 + dy;
            for (var r = 0; r < rows.Count; r++)
            {
                var rowHeight = rows[r].Max(id => sizes[id].Height);
                var x = cursor;
                for (var k = 0; k < rows[r].Length; k++)
                {
                    Place(rows[r][k], x, rowTop + (rowHeight - sizes[rows[r][k]].Height) / 2, c + r * o.WrapAfter + k, 0);
                    x += slotWidths[k] + o.ColumnGap;
                }
                if (r + 1 < rows.Count)
                {
                    wrapLanes[(rows[r][^1], rows[r + 1][0])] = rowTop + rowHeight + o.WrapRowGap / 2;
                    rowTop += rowHeight + o.WrapRowGap;
                }
            }
            cursor += slotWidths.Sum() + o.ColumnGap * o.WrapAfter;
            c += length - 1;
            if (c + 1 < columns.Count)
                dy = rowTop - (o.Padding + (tallest - heights[c + 1]) / 2);
        }

        var byId = boxes.Where(b => b.Card is null).ToDictionary(b => b.Id, b => b.Bounds, StringComparer.Ordinal);
        var routes = new List<EdgeRoute>();
        foreach (var (from, to, when) in edges)
        {
            if (byId.TryGetValue(from, out var a) && byId.TryGetValue(to, out var b))
                routes.Add(new EdgeRoute(from, to, when, new Point(a.Right, a.CenterY), new Point(b.X, b.CenterY),
                    wrapLanes.TryGetValue((from, to), out var lane) ? lane : null));
        }

        var right = boxes.Count == 0 ? o.Padding : boxes.Max(b => b.Bounds.Right);
        var bottom = boxes.Count == 0 ? o.Padding : boxes.Max(b => b.Bounds.Bottom);
        return new FlowArrangement(boxes, routes, columns, new Size(right + o.Padding, bottom + o.Padding));
    }

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
