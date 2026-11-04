namespace DazeControl.Core.Model;

/// <summary>
/// What a flow's edges make of its nodes, beyond the graph itself (D-065). Ids cover units (steps outside cards and
/// cards) and card members, which follow their card.
/// </summary>
public static class FlowShape
{
    /// <summary>
    /// Fallback steps: a unit with an incoming <c>when: failure</c> edge, or an incoming <c>when: success</c> edge
    /// from a fallback step. They only run when something failed, so a run where they didn't is no problem.
    /// </summary>
    public static IReadOnlySet<string> Fallbacks(Flow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var fallbacks = new HashSet<string>(StringComparer.Ordinal);
        // A valid flow is a DAG over units: at most one pass per unit adds anything.
        for (var changed = true; changed;)
        {
            changed = false;
            foreach (var node in flow.Nodes)
            {
                if (!fallbacks.Contains(node.Id) && node.After.Any(e =>
                        e.When == EdgeWhen.Failure || (e.When == EdgeWhen.Success && fallbacks.Contains(e.From))))
                    changed = fallbacks.Add(node.Id);
            }
        }
        return WithMembers(flow, fallbacks);
    }

    /// <summary>Units with an outgoing <c>when: failure</c> edge (and their members): their failure is handled.</summary>
    public static IReadOnlySet<string> WithFallback(Flow flow)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var sources = flow.Nodes.SelectMany(n => n.After).Where(e => e.When == EdgeWhen.Failure).Select(e => e.From);
        return WithMembers(flow, new HashSet<string>(sources, StringComparer.Ordinal));
    }

    /// <summary>The unit a node belongs to: its card for a member, else itself.</summary>
    public static string UnitOf(Flow flow, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.Nodes.OfType<GroupNode>().FirstOrDefault(c => c.Members.Contains(nodeId))?.Id ?? nodeId;
    }

    private static HashSet<string> WithMembers(Flow flow, HashSet<string> units)
    {
        foreach (var card in flow.Nodes.OfType<GroupNode>().Where(c => units.Contains(c.Id)).ToList())
            units.UnionWith(card.Members);
        return units;
    }
}
