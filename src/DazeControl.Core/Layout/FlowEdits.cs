using System.Globalization;
using DazeControl.Core.Model;

namespace DazeControl.Core.Layout;

/// <summary>
/// The outcome of a "run together" edit: the new flow, or the old one with a refusal. <paramref name="DroppedFallback"/>
/// says an "if it fails" / "whatever happens" link between steps that now start together was removed (D-058).
/// </summary>
public readonly record struct GroupResult(Flow Flow, ConnectRefusal Refusal, bool DroppedFallback = false)
{
    public static implicit operator GroupResult((Flow Flow, ConnectRefusal Refusal) result) => new(result.Flow, result.Refusal);

    public void Deconstruct(out Flow flow, out ConnectRefusal refusal)
    {
        flow = Flow;
        refusal = Refusal;
    }
}

/// <summary>
/// Card and sequence edits behind the canvas gestures (UX §5.2), pure so they are tested here. Each returns the
/// new flow, or the old one with a refusal when the result would be a loop or would change which failures count.
/// </summary>
public static class FlowEdits
{
    public const string CardIdBase = "together";

    /// <summary>
    /// "Run together" (Ctrl+G): one card holding <paramref name="ids"/>' steps in that order. Cards among them merge in
    /// (the first one keeps its id); members of other cards and unknown ids are ignored. The members' outer edges move
    /// to the card and whatever waited for a member waits for the card. Links between the steps go: they start together
    /// now, which is how a wrong sequence gets fixed (D-058).
    /// </summary>
    public static GroupResult Group(Flow flow, IEnumerable<string> ids)
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
            return new(flow, ConnectRefusal.None);

        string Top(string id) => cardOf.GetValueOrDefault(id, id);
        var inside = absorbed.ToHashSet(StringComparer.Ordinal);
        var nodes = absorbed.Select(id => flow.FindNode(id)!).ToList();
        var inner = nodes.SelectMany(n => n.After).Where(e => inside.Contains(Top(e.From))).ToList();

        // One card edge per source: two different conditions on the same source can't both survive.
        var incoming = nodes.SelectMany(n => n.After).Where(e => !inside.Contains(Top(e.From))).Select(e => e with { From = Top(e.From) }).ToList();
        if (Conflicts(incoming) || flow.Nodes.Where(n => !inside.Contains(n.Id) && !members.Contains(n.Id))
                .Any(n => Conflicts(n.After.Where(e => inside.Contains(Top(e.From))).Select(e => e with { From = "card" }))))
            return new(flow, ConnectRefusal.Conditions);
        // "If X fails" on an absorbed step would become "if any step in the card fails".
        if (flow.Nodes.Any(n => !inside.Contains(n.Id) && n.After.Any(e => e.When == EdgeWhen.Failure && inside.Contains(Top(e.From)))))
            return new(flow, ConnectRefusal.FailureFallback);
        // In a card only a step's own "may fail" counts; the card's policy handles every other failure (Scheduler).
        var policies = nodes.Where(n => n is GroupNode || n.OnFailure != FailurePolicy.Ignore).Select(n => n.OnFailure).Distinct().ToList();
        if (policies.Count > 1)
            return new(flow, ConnectRefusal.FailurePolicies);

        var cardId = absorbed.FirstOrDefault(id => flow.FindNode(id) is GroupNode) ?? NewCardId(flow);
        var existing = flow.FindNode(cardId) as GroupNode;
        var after = Merge(incoming);
        var card = new GroupNode(cardId, [.. members])
        {
            Name = existing?.Name,
            OnFailure = policies.Count == 1 ? policies[0] : FailurePolicy.Stop,
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
        return FlowGraph.HasLoop(grouped)
            ? new(flow, ConnectRefusal.Loop)
            : new(grouped, ConnectRefusal.None, inner.Any(e => e.When != EdgeWhen.Success));
    }

    /// <summary>
    /// Ctrl+Shift+G: the card goes; its members take its edges and its dependents wait for each member. Refused while
    /// something runs only if the card fails: that means "any step failed", which per-step edges can't say.
    /// </summary>
    public static (Flow Flow, ConnectRefusal Refusal) Ungroup(Flow flow, string cardId)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.FindNode(cardId) is not GroupNode card)
            return (flow, ConnectRefusal.None);
        var cardOf = FlowLayout.CardOf(flow);
        var members = card.Members.Where(m => cardOf.GetValueOrDefault(m) == cardId).ToList();
        if (members.Count > 1 && HasFailureFallback(flow, cardId)) // one step: "card failed" is "that step failed"
            return (flow, ConnectRefusal.FailureFallback);
        return (flow with
        {
            Nodes = [.. flow.Nodes.Where(n => n.Id != cardId).Select(n =>
                members.Contains(n.Id) ? Leave(n, card) with { After = card.After }
                : n with { After = Merge(n.After.SelectMany(e => e.From == cardId ? members.Select(m => e with { From = m }) : [e])) })],
        }, ConnectRefusal.None);
    }

    /// <summary>
    /// Drags a member out of its card: it takes the card's edges; a card left with one step dissolves. Refused while
    /// something runs only if the card fails (the step's failure would stop counting).
    /// </summary>
    public static (Flow Flow, ConnectRefusal Refusal) TakeOut(Flow flow, string memberId)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var cardOf = FlowLayout.CardOf(flow);
        if (!cardOf.TryGetValue(memberId, out var cardId) || flow.FindNode(cardId) is not GroupNode card)
            return (flow, ConnectRefusal.None);
        if (HasFailureFallback(flow, cardId))
            return (flow, ConnectRefusal.FailureFallback);
        var rest = card.Members.Where(m => m != memberId).ToList();
        var taken = flow with
        {
            Nodes = [.. flow.Nodes.Select(n =>
                n.Id == memberId ? Leave(n, card) with { After = card.After }
                : n.Id == cardId ? card with { Members = [.. rest] }
                : n)],
        };
        return rest.Count(m => cardOf.GetValueOrDefault(m) == cardId) < 2 ? Ungroup(taken, cardId) : (taken, ConnectRefusal.None);
    }

    private static bool HasFailureFallback(Flow flow, string cardId) =>
        flow.Nodes.Any(n => n.After.Any(e => e.From == cardId && e.When == EdgeWhen.Failure));

    /// <summary>
    /// A step leaving a card keeps how its failure was handled: its own "may fail", else the card's policy (the rule
    /// in Scheduler.CompleteMemberOf; change both together).
    /// </summary>
    private static Node Leave(Node step, GroupNode card) =>
        step.OnFailure == FailurePolicy.Ignore ? step : step with { OnFailure = card.OnFailure };

    /// <summary>
    /// Drop on a pill's body: run together with it (into its card, or a new card for the two). The dragged step leaves
    /// its place in any sequence with the target: its links on a path to or from the target go (D-058).
    /// </summary>
    public static GroupResult RunTogether(Flow flow, string dragged, string target)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.FindNode(dragged) is not StepNode || dragged == target)
            return new(flow, ConnectRefusal.None);
        var cardOf = FlowLayout.CardOf(flow);
        var targetTop = cardOf.GetValueOrDefault(target, target);
        if (cardOf.GetValueOrDefault(dragged) == targetTop)
            return new(flow, ConnectRefusal.None); // already in that card
        var (free, refusal) = TakeOut(flow, dragged);
        if (refusal != ConnectRefusal.None)
            return new(flow, refusal);
        if (free.FindNode(targetTop) is null) // the drag dissolved the target's card: target is a plain step now
            targetTop = target;

        // What runs after the target can't come before the dragged step, nor what leads to the target after it.
        var freeCardOf = FlowLayout.CardOf(free);
        string Top(string id) => freeCardOf.GetValueOrDefault(id, id);
        var afterTarget = FlowGraph.Downstream(free, targetTop);
        bool LeadsToTarget(string id) => id == targetTop || FlowGraph.Downstream(free, id).Contains(targetTop);
        var dropped = new List<Edge>();
        var unlinked = free with
        {
            Nodes = [.. free.Nodes.Select(n =>
            {
                var cut = n.Id == dragged
                    ? n.After.Where(e => Top(e.From) == targetTop || afterTarget.Contains(Top(e.From))).ToList()
                    : n.After.Where(e => e.From == dragged && LeadsToTarget(n.Id)).ToList();
                dropped.AddRange(cut);
                return cut.Count == 0 ? n : n with { After = [.. n.After.Except(cut)] };
            })],
        };
        var result = Group(unlinked, [targetTop, dragged]);
        return result.Refusal == ConnectRefusal.None
            ? result with { DroppedFallback = result.DroppedFallback || dropped.Any(e => e.When != EdgeWhen.Success) }
            : new(flow, result.Refusal);
    }

    /// <summary>Drop on a pill's right third: run after it (the dragged step then waits only for it).</summary>
    public static (Flow Flow, ConnectRefusal Refusal) RunAfter(Flow flow, string dragged, string target)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var cardOf = FlowLayout.CardOf(flow);
        var targetTop = cardOf.GetValueOrDefault(target, target);
        if (flow.FindNode(dragged) is null || flow.FindNode(target) is null || cardOf.GetValueOrDefault(dragged, dragged) == targetTop)
            return (flow, cardOf.TryGetValue(dragged, out var own) && own == targetTop ? ConnectRefusal.SameCard : ConnectRefusal.None);
        var (free, refusal) = TakeOut(flow, dragged);
        if (refusal != ConnectRefusal.None)
            return (flow, refusal);
        if (free.FindNode(targetTop) is null)
            targetTop = target;
        var result = free with { Nodes = [.. free.Nodes.Select(n => n.Id == dragged ? n with { After = [new Edge(targetTop)] } : n)] };
        return FlowGraph.HasLoop(result) ? (flow, ConnectRefusal.Loop) : (result, ConnectRefusal.None);
    }

    /// <summary>
    /// Drop on a pill's left third: run before it (D-083). The dragged step takes all of the target's incoming edges,
    /// kinds kept, replacing its own; the target then waits only for it. Its dependents stay as they are.
    /// <see cref="GroupResult.DroppedFallback"/> says an "if it fails" / "whatever happens" link of its own went.
    /// </summary>
    public static GroupResult RunBefore(Flow flow, string dragged, string target)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var cardOf = FlowLayout.CardOf(flow);
        var targetTop = cardOf.GetValueOrDefault(target, target);
        if (flow.FindNode(dragged) is null || flow.FindNode(target) is null || cardOf.GetValueOrDefault(dragged, dragged) == targetTop)
            return new(flow, cardOf.TryGetValue(dragged, out var own) && own == targetTop ? ConnectRefusal.SameCard : ConnectRefusal.None);
        var (free, refusal) = TakeOut(flow, dragged);
        if (refusal != ConnectRefusal.None)
            return new(flow, refusal);
        if (free.FindNode(targetTop) is null)
            targetTop = target;
        var incoming = free.FindNode(targetTop)!.After;
        var lost = free.FindNode(dragged)!.After.Any(e => e.When != EdgeWhen.Success && incoming.All(i => i.From != e.From || i.When != e.When));
        var result = free with
        {
            Nodes = [.. free.Nodes.Select(n =>
                n.Id == dragged ? n with { After = [.. incoming.Where(e => e.From != dragged)] }
                : n.Id == targetTop ? n with { After = [new Edge(dragged)] }
                : n)],
        };
        return FlowGraph.HasLoop(result) ? new(flow, ConnectRefusal.Loop) : new(result, ConnectRefusal.None, lost);
    }

    private static bool Conflicts(IEnumerable<Edge> edges) =>
        edges.GroupBy(e => e.From, StringComparer.Ordinal).Any(g => g.Select(e => e.When).Distinct().Count() > 1);

    private static ValueList<Edge> Merge(IEnumerable<Edge> edges) => [.. edges.DistinctBy(e => e.From, StringComparer.Ordinal)];

    private static string NewCardId(Flow flow)
    {
        var id = CardIdBase;
        for (var n = 2; flow.FindNode(id) is not null; n++)
            id = string.Create(CultureInfo.InvariantCulture, $"{CardIdBase}-{n}");
        return id;
    }
}
