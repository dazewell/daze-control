using System.Globalization;
using DazeControl.Core.Model;

namespace DazeControl.Core.Layout;

/// <summary>
/// Card and sequence edits behind the canvas gestures (UX §5.2), pure so they are tested here. Each returns the
/// new flow, or the old one with a refusal when the result would be a loop or a card whose steps wait for each other.
/// </summary>
public static class FlowEdits
{
    public const string CardIdBase = "together";

    /// <summary>
    /// "Run together" (Ctrl+G): one card holding <paramref name="ids"/>' steps in that order. Cards among them merge in
    /// (the first one keeps its id); members of other cards and unknown ids are ignored. The members' outer edges move
    /// to the card and whatever waited for a member waits for the card.
    /// </summary>
    public static (Flow Flow, ConnectRefusal Refusal) Group(Flow flow, IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(ids);
        var cardOf = FlowLayout.CardOf(flow);
        var absorbed = new List<string>(); // top-level ids that become the card
        var members = new List<string>();
        foreach (var id in ids.Distinct(StringComparer.Ordinal))
        {
            switch (flow.FindNode(id))
            {
                case StepNode when !cardOf.ContainsKey(id):
                    absorbed.Add(id);
                    members.Add(id);
                    break;
                case GroupNode group:
                    absorbed.Add(id);
                    members.AddRange(group.Members.Where(m => cardOf.GetValueOrDefault(m) == group.Id && !members.Contains(m)));
                    break;
            }
        }
        if (members.Count < 2)
            return (flow, ConnectRefusal.None);

        string Top(string id) => cardOf.GetValueOrDefault(id, id);
        var inside = absorbed.ToHashSet(StringComparer.Ordinal);
        var nodes = absorbed.Select(id => flow.FindNode(id)!).ToList();
        if (nodes.Any(n => n.After.Any(e => inside.Contains(Top(e.From)))))
            return (flow, ConnectRefusal.SameCard);

        var cardId = absorbed.FirstOrDefault(id => flow.FindNode(id) is GroupNode) ?? NewCardId(flow);
        var existing = flow.FindNode(cardId) as GroupNode;
        var after = Merge(nodes.SelectMany(n => n.After).Select(e => e with { From = Top(e.From) }));
        var card = new GroupNode(cardId, [.. members])
        {
            Name = existing?.Name,
            OnFailure = existing?.OnFailure ?? FailurePolicy.Stop,
            After = after,
        };

        var result = new List<Node>();
        foreach (var node in flow.Nodes)
        {
            if (node.Id == absorbed[0])
                result.Add(card);
            if (inside.Contains(node.Id) && node is GroupNode)
                continue; // merged into the card
            if (members.Contains(node.Id))
                result.Add(node with { After = [] });
            else
                result.Add(node with { After = Merge(node.After.Select(e => inside.Contains(Top(e.From)) ? e with { From = cardId } : e)) });
        }
        var grouped = flow with { Nodes = [.. result] };
        return FlowGraph.HasLoop(grouped) ? (flow, ConnectRefusal.Loop) : (grouped, ConnectRefusal.None);
    }

    /// <summary>Ctrl+Shift+G: the card goes; its members take its edges and its dependents wait for each member.</summary>
    public static Flow Ungroup(Flow flow, string cardId)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.FindNode(cardId) is not GroupNode card)
            return flow;
        var cardOf = FlowLayout.CardOf(flow);
        var members = card.Members.Where(m => cardOf.GetValueOrDefault(m) == cardId).ToList();
        return flow with
        {
            Nodes = [.. flow.Nodes.Where(n => n.Id != cardId).Select(n =>
                members.Contains(n.Id) ? n with { After = card.After }
                : n with { After = Merge(n.After.SelectMany(e => e.From == cardId ? members.Select(m => e with { From = m }) : [e])) })],
        };
    }

    /// <summary>Drags a member out of its card: it takes the card's edges; a card left with one step dissolves.</summary>
    public static Flow TakeOut(Flow flow, string memberId)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var cardOf = FlowLayout.CardOf(flow);
        if (!cardOf.TryGetValue(memberId, out var cardId) || flow.FindNode(cardId) is not GroupNode card)
            return flow;
        var rest = card.Members.Where(m => m != memberId).ToList();
        var taken = flow with
        {
            Nodes = [.. flow.Nodes.Select(n =>
                n.Id == memberId ? n with { After = card.After }
                : n.Id == cardId ? card with { Members = [.. rest] }
                : n)],
        };
        return rest.Count(m => cardOf.GetValueOrDefault(m) == cardId) < 2 ? Ungroup(taken, cardId) : taken;
    }

    /// <summary>Drop on a pill's body: run together with it (into its card, or a new card for the two).</summary>
    public static (Flow Flow, ConnectRefusal Refusal) RunTogether(Flow flow, string dragged, string target)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.FindNode(dragged) is not StepNode || dragged == target)
            return (flow, ConnectRefusal.None);
        var cardOf = FlowLayout.CardOf(flow);
        var targetTop = cardOf.GetValueOrDefault(target, target);
        if (cardOf.GetValueOrDefault(dragged) == targetTop)
            return (flow, ConnectRefusal.None); // already in that card
        var free = TakeOut(flow, dragged);
        if (free.FindNode(targetTop) is null) // the drag dissolved the target's card: target is a plain step now
            targetTop = target;
        var result = Group(free, [targetTop, dragged]);
        return result.Refusal == ConnectRefusal.None ? result : (flow, result.Refusal);
    }

    /// <summary>Drop on a pill's right third: run after it (the dragged step then waits only for it).</summary>
    public static (Flow Flow, ConnectRefusal Refusal) RunAfter(Flow flow, string dragged, string target)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var cardOf = FlowLayout.CardOf(flow);
        var targetTop = cardOf.GetValueOrDefault(target, target);
        if (flow.FindNode(dragged) is null || flow.FindNode(target) is null || cardOf.GetValueOrDefault(dragged, dragged) == targetTop)
            return (flow, cardOf.TryGetValue(dragged, out var own) && own == targetTop ? ConnectRefusal.SameCard : ConnectRefusal.None);
        var free = TakeOut(flow, dragged);
        if (free.FindNode(targetTop) is null)
            targetTop = target;
        var result = free with { Nodes = [.. free.Nodes.Select(n => n.Id == dragged ? n with { After = [new Edge(targetTop)] } : n)] };
        return FlowGraph.HasLoop(result) ? (flow, ConnectRefusal.Loop) : (result, ConnectRefusal.None);
    }

    private static ValueList<Edge> Merge(IEnumerable<Edge> edges) => [.. edges.DistinctBy(e => e.From, StringComparer.Ordinal)];

    private static string NewCardId(Flow flow)
    {
        var id = CardIdBase;
        for (var n = 2; flow.FindNode(id) is not null; n++)
            id = string.Create(CultureInfo.InvariantCulture, $"{CardIdBase}-{n}");
        return id;
    }
}
