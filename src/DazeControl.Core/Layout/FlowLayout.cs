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
}

/// <param name="Card">The card a member sits in; null for a top-level step or a card.</param>
/// <param name="Column">The column of the node (a member: its card's).</param>
/// <param name="Row">Index within the column (a member: within its card).</param>
public sealed record NodeBox(string Id, Rect Bounds, int Column, int Row, string? Card);

/// <summary>An edge between two top-level boxes; an edge naming a card member is drawn from/to its card.</summary>
public sealed record EdgeRoute(string From, string To, EdgeWhen When, Point Start, Point End);

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
/// the tallest. Works on invalid flows too (autosave keeps half-built ones): cycles and unknown ids are ignored.
/// </summary>
public static class FlowLayout
{
    /// <summary>Card id for each member id (the first card listing a step wins, as in validation).</summary>
    public static Dictionary<string, string> CardOf(Flow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
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

        var boxes = new List<NodeBox>();
        var x = o.Padding;
        for (var c = 0; c < columns.Count; c++)
        {
            var y = o.Padding + (tallest - heights[c]) / 2;
            for (var r = 0; r < columns[c].Count; r++)
            {
                var id = columns[c][r];
                var size = sizes[id];
                boxes.Add(new NodeBox(id, new Rect(x, y, size.Width, size.Height), c, r, null));
                if (membersOf.TryGetValue(id, out var members))
                {
                    var my = y + o.CardHeader + o.CardPadding;
                    for (var m = 0; m < members.Count; m++)
                    {
                        var h = measure(members[m]).Height;
                        boxes.Add(new NodeBox(members[m], new Rect(x + o.CardPadding, my, size.Width - 2 * o.CardPadding, h), c, m, id));
                        my += h + o.MemberSpacing;
                    }
                }
                y += size.Height + o.RowGap;
            }
            x += (columns[c].Count == 0 ? 0 : columns[c].Max(id => sizes[id].Width)) + o.ColumnGap;
        }

        var byId = boxes.Where(b => b.Card is null).ToDictionary(b => b.Id, b => b.Bounds, StringComparer.Ordinal);
        var routes = new List<EdgeRoute>();
        foreach (var (from, to, when) in edges)
        {
            if (byId.TryGetValue(from, out var a) && byId.TryGetValue(to, out var b))
                routes.Add(new EdgeRoute(from, to, when, new Point(a.Right, a.CenterY), new Point(b.X, b.CenterY)));
        }

        var right = boxes.Count == 0 ? o.Padding : boxes.Max(b => b.Bounds.Right);
        var bottom = boxes.Count == 0 ? o.Padding : boxes.Max(b => b.Bounds.Bottom);
        return new FlowArrangement(boxes, routes, columns, new Size(right + o.Padding, bottom + o.Padding));
    }
}
