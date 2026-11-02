using DazeControl.Core.Model;

namespace DazeControl.Core.Layout;

/// <summary>Why the canvas refuses a connection (UX §5.7 #3, #4).</summary>
public enum ConnectRefusal
{
    None,

    /// <summary>The edge exists already.</summary>
    Exists,

    /// <summary>"That would make a loop. A step can't wait for something that waits for it."</summary>
    Loop,

    /// <summary>"Steps in a card start together, so they can't wait for each other."</summary>
    SameCard,

    /// <summary>An id that isn't in the flow.</summary>
    Unknown,

    /// <summary>Grouping would merge "wait for X" edges with different conditions into one.</summary>
    Conditions,

    /// <summary>Ungrouping would turn "if the card fails" (any step) into "if every step fails".</summary>
    FailureFallback,
}

/// <summary>
/// Graph questions the editor asks before it changes edges, on the top-level graph the canvas draws (a card
/// member's edges are its card's, as in <see cref="FlowLayout.Edges"/>).
/// </summary>
public static class FlowGraph
{
    /// <summary>The canvas node an id is drawn as: a member's card, else itself.</summary>
    public static string TopLevel(Flow flow, string id) => FlowLayout.CardOf(flow).GetValueOrDefault(id, id);

    /// <summary>Whether <paramref name="to"/> may wait for <paramref name="from"/> (both mapped to their cards).</summary>
    public static ConnectRefusal CanConnect(Flow flow, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.FindNode(from) is null || flow.FindNode(to) is null)
            return ConnectRefusal.Unknown;
        var cardOf = FlowLayout.CardOf(flow);
        var a = cardOf.GetValueOrDefault(from, from);
        var b = cardOf.GetValueOrDefault(to, to);
        if (a == b)
            return cardOf.ContainsKey(from) || cardOf.ContainsKey(to) ? ConnectRefusal.SameCard : ConnectRefusal.Loop;
        var edges = FlowLayout.Edges(flow, cardOf);
        if (edges.Any(e => e.From == a && e.To == b))
            return ConnectRefusal.Exists;
        return Reaches(edges, b, a) ? ConnectRefusal.Loop : ConnectRefusal.None;
    }

    /// <summary>Everything that (indirectly) waits for <paramref name="id"/>: picking any of them as its source loops.</summary>
    public static HashSet<string> Downstream(Flow flow, string id)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var cardOf = FlowLayout.CardOf(flow);
        var edges = FlowLayout.Edges(flow, cardOf);
        var start = cardOf.GetValueOrDefault(id, id);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>([start]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            foreach (var e in edges)
            {
                if (e.From == node && seen.Add(e.To))
                    stack.Push(e.To);
            }
        }
        return seen;
    }

    /// <summary>Whether the drawn graph has a loop (validation reports it; edits must not create one).</summary>
    public static bool HasLoop(Flow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var cardOf = FlowLayout.CardOf(flow);
        var edges = FlowLayout.Edges(flow, cardOf);
        // Kahn's topological sort: a node never freed sits on (or behind) a loop.
        var incoming = new Dictionary<string, int>(StringComparer.Ordinal);
        var outgoing = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var (from, to, _) in edges)
        {
            incoming[to] = incoming.GetValueOrDefault(to) + 1;
            incoming.TryAdd(from, 0);
            if (!outgoing.TryGetValue(from, out var list))
                outgoing[from] = list = [];
            list.Add(to);
        }
        var ready = new Queue<string>(incoming.Where(p => p.Value == 0).Select(p => p.Key));
        var freed = 0;
        while (ready.TryDequeue(out var node))
        {
            freed++;
            foreach (var next in outgoing.GetValueOrDefault(node) ?? [])
            {
                if (--incoming[next] == 0)
                    ready.Enqueue(next);
            }
        }
        return freed < incoming.Count;
    }

    private static bool Reaches(IReadOnlyList<(string From, string To, EdgeWhen When)> edges, string start, string target)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal) { start };
        var stack = new Stack<string>([start]);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node == target)
                return true;
            foreach (var e in edges)
            {
                if (e.From == node && seen.Add(e.To))
                    stack.Push(e.To);
            }
        }
        return false;
    }

    /// <summary>
    /// Adds "<paramref name="to"/> waits for <paramref name="from"/>" with both ends mapped to their cards; the flow
    /// unchanged when <see cref="CanConnect"/> refuses.
    /// </summary>
    public static Flow Connect(Flow flow, string from, string to, EdgeWhen when = EdgeWhen.Success)
    {
        if (CanConnect(flow, from, to) != ConnectRefusal.None)
            return flow;
        var a = TopLevel(flow, from);
        var b = TopLevel(flow, to);
        return flow with { Nodes = [.. flow.Nodes.Select(n => n.Id == b ? n with { After = [.. n.After, new Edge(a, when)] } : n)] };
    }

    /// <summary>Removes the drawn edge <paramref name="from"/> → <paramref name="to"/> (top-level ids).</summary>
    public static Flow Disconnect(Flow flow, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var cardOf = FlowLayout.CardOf(flow);
        return flow with
        {
            Nodes = [.. flow.Nodes.Select(n => cardOf.GetValueOrDefault(n.Id, n.Id) == to && n.After.Any(e => cardOf.GetValueOrDefault(e.From, e.From) == from)
                ? n with { After = [.. n.After.Where(e => cardOf.GetValueOrDefault(e.From, e.From) != from)] }
                : n)],
        };
    }

    /// <summary>"Only if this fails" / "Whatever happens" on the drawn edge <paramref name="from"/> → <paramref name="to"/>.</summary>
    public static Flow SetWhen(Flow flow, string from, string to, EdgeWhen when)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var cardOf = FlowLayout.CardOf(flow);
        return flow with
        {
            Nodes = [.. flow.Nodes.Select(n => cardOf.GetValueOrDefault(n.Id, n.Id) == to
                ? n with { After = [.. n.After.Select(e => cardOf.GetValueOrDefault(e.From, e.From) == from ? e with { When = when } : e)] }
                : n)],
        };
    }
}
