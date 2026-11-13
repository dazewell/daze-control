using CtrlAltDaze.Core.Model;

namespace CtrlAltDaze.Core.Layout;

/// <summary>
/// Layout › "Untangle lines" (D-067, was "Tidy up", X-006): reorders each column to cut edge crossings. Layout stays automatic (D-020), so the only
/// lever is the order in <see cref="Flow.Nodes"/>; a column's nodes swap among the slots they already hold, so
/// other nodes and card members don't move in the file.
/// </summary>
/// <summary>Whether Untangle lines can help (D-067): it's off with a reason otherwise.</summary>
public enum UntangleState
{
    Helps,

    /// <summary>"No lines cross".</summary>
    NoCrossings,

    /// <summary>"Can't untangle these any further".</summary>
    NoBetterOrder,
}

public static class FlowTidy
{
    private const int Sweeps = 4;

    /// <summary>The flow with columns reordered, or the same instance when no order has fewer crossings.</summary>
    /// <param name="align">Rows compare across columns as the layout lines them up.</param>
    public static Flow Tidy(Flow flow, LayoutAlign align = LayoutAlign.Centre)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var graph = Graph.Of(flow, align);
        var best = graph.Columns.Select(c => c.ToList()).ToList();
        var fewest = graph.Crossings(best);
        var current = best.Select(c => c.ToList()).ToList();
        for (var sweep = 0; sweep < Sweeps && fewest > 0; sweep++)
        {
            var forward = sweep % 2 == 0;
            var order = forward ? Enumerable.Range(1, current.Count - 1) : Enumerable.Range(0, current.Count - 1).Reverse();
            foreach (var c in order)
            {
                var y = graph.Positions(current);
                var neighbours = forward ? graph.Incoming : graph.Outgoing;
                current[c] = [.. current[c].OrderBy(id => neighbours[id] is { Count: > 0 } n ? n.Average(m => y[m]) : y[id])];
            }
            var crossings = graph.Crossings(current);
            if (crossings < fewest)
            {
                fewest = crossings;
                best = current.Select(c => c.ToList()).ToList();
            }
        }
        if (best.Zip(graph.Columns).All(p => p.First.SequenceEqual(p.Second, StringComparer.Ordinal)))
            return flow;

        // Each column refills the slots its nodes held, top to bottom.
        var nodes = flow.Nodes.ToList();
        var byId = nodes.Where(n => graph.Layer.ContainsKey(n.Id)).GroupBy(n => n.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        for (var c = 0; c < best.Count; c++)
        {
            var slots = graph.Columns[c].Select(id => nodes.FindIndex(n => n.Id == id)).Order().ToList();
            for (var i = 0; i < slots.Count; i++)
                nodes[slots[i]] = byId[best[c][i]];
        }
        return flow with { Nodes = [.. nodes] };
    }

    /// <summary>Edge crossings in the current order.</summary>
    public static int Crossings(Flow flow, LayoutAlign align = LayoutAlign.Centre)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var graph = Graph.Of(flow, align);
        return graph.Crossings(graph.Columns);
    }

    /// <summary>What Untangle lines would do to <paramref name="flow"/> now.</summary>
    public static UntangleState Check(Flow flow, LayoutAlign align = LayoutAlign.Centre) =>
        Crossings(flow, align) == 0 ? UntangleState.NoCrossings
        : ReferenceEquals(Tidy(flow, align), flow) ? UntangleState.NoBetterOrder
        : UntangleState.Helps;

    private sealed record Graph(
        IReadOnlyList<IReadOnlyList<string>> Columns,
        Dictionary<string, int> Layer,
        IReadOnlyList<(string From, string To)> Edges,
        Dictionary<string, List<string>> Incoming,
        Dictionary<string, List<string>> Outgoing,
        LayoutAlign Align)
    {
        public static Graph Of(Flow flow, LayoutAlign align)
        {
            var cardOf = FlowLayout.CardOf(flow);
            var layer = FlowLayout.Layers(flow);
            var count = layer.Count == 0 ? 0 : layer.Values.Max() + 1;
            var columns = Enumerable.Range(0, count).Select(_ => new List<string>()).ToList();
            foreach (var node in flow.Nodes)
            {
                if (layer.TryGetValue(node.Id, out var l) && !columns[l].Contains(node.Id))
                    columns[l].Add(node.Id);
            }
            // Edges against the layering (a cycle in a half-built flow) don't order anything.
            var edges = FlowLayout.Edges(flow, cardOf)
                .Where(e => layer.TryGetValue(e.From, out var a) && layer.TryGetValue(e.To, out var b) && a < b)
                .Select(e => (e.From, e.To)).ToList();
            var incoming = layer.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
            var outgoing = layer.Keys.ToDictionary(id => id, _ => new List<string>(), StringComparer.Ordinal);
            foreach (var (from, to) in edges)
            {
                incoming[to].Add(from);
                outgoing[from].Add(to);
            }
            return new Graph(columns, layer, edges, incoming, outgoing, align);
        }

        /// <summary>
        /// Row relative to the column's middle when columns are centred (FlowLayout), else to its top, so rows
        /// compare across columns.
        /// </summary>
        public Dictionary<string, double> Positions(IReadOnlyList<IReadOnlyList<string>> columns)
        {
            var y = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var column in columns)
            {
                for (var r = 0; r < column.Count; r++)
                    y[column[r]] = Align == LayoutAlign.Top ? r : r - (column.Count - 1) / 2.0;
            }
            return y;
        }

        public int Crossings(IReadOnlyList<IReadOnlyList<string>> columns)
        {
            var y = Positions(columns);
            var crossings = 0;
            for (var i = 0; i < Edges.Count; i++)
            {
                for (var j = i + 1; j < Edges.Count; j++)
                {
                    if (Cross(Edges[i], Edges[j], y))
                        crossings++;
                }
            }
            return crossings;
        }

        // Straight lines in (column, row) space; edges sharing an end never count.
        private bool Cross((string From, string To) a, (string From, string To) b, Dictionary<string, double> y)
        {
            if (a.From == b.From || a.To == b.To)
                return false;
            double start = Math.Max(Layer[a.From], Layer[b.From]), end = Math.Min(Layer[a.To], Layer[b.To]);
            if (start >= end)
                return false;
            double At((string From, string To) e, double x) =>
                y[e.From] + (y[e.To] - y[e.From]) * (x - Layer[e.From]) / (Layer[e.To] - Layer[e.From]);
            var before = At(a, start) - At(b, start);
            var after = At(a, end) - At(b, end);
            return before * after < 0;
        }
    }
}
