using System.Globalization;
using System.Text.RegularExpressions;
using CtrlAltDaze.Core.Model;

namespace CtrlAltDaze.Core.Layout;

/// <summary>
/// Steps copied from a flow (UX §5.2 Ctrl+C): the nodes in flow order with every edge they had. Library items stay
/// referenced by key: the clipboard lives in the editor, so a paste lands in the same configuration (D-039).
/// </summary>
public sealed record FlowClip(ValueList<Node> Nodes);

/// <summary>Add, insert, delete, copy and paste behind the canvas commands (UX §5.2), pure so they are tested here.</summary>
public static partial class FlowCommands
{
    /// <summary>"audio.setDefault" → "setDefault", made unique in the flow.</summary>
    public static string NewStepId(Flow flow, string step)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(step);
        var name = step.Split('.')[^1];
        return Unique(name.Length > 0 ? name : "step", id => flow.FindNode(id) is not null);
    }

    /// <summary>A new step running after <paramref name="after"/> (their cards for members); empty = a first step.</summary>
    public static (Flow Flow, string Id) Add(Flow flow, string step, IEnumerable<string> after)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(after);
        var id = NewStepId(flow, step);
        var node = new StepNode(id, step) { After = [.. Anchors(flow, after).Select(a => new Edge(a))] };
        return (flow with { Nodes = [.. flow.Nodes, node] }, id);
    }

    /// <summary>"+" on an edge: a new step between <paramref name="from"/> and <paramref name="to"/>; it takes the edge's condition.</summary>
    public static (Flow Flow, string? Id) Insert(Flow flow, string step, string from, string to)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.FindNode(to) is not { } target || target.After.FirstOrDefault(e => e.From == from) is not { } edge)
            return (flow, null);
        var id = NewStepId(flow, step);
        var node = new StepNode(id, step) { After = [edge] };
        return (flow with
        {
            Nodes = [.. flow.Nodes.Select(n => n.Id == to ? n with { After = [.. n.After.Select(e => e.From == from ? new Edge(id) : e)] } : n), node],
        }, id);
    }

    /// <summary>
    /// Del: removes the steps and cards (with their members) and reconnects around them: in A → B → C, deleting B
    /// makes C wait for A, under the condition B waited with. An "only if B fails" edge goes with B. A card left with
    /// one step dissolves; one left with none goes too.
    /// </summary>
    /// <summary>What deleting <paramref name="ids"/> removes: them, a card's members, and a card left with no members.</summary>
    public static HashSet<string> DeletedBy(Flow flow, IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(ids);
        var cardOf = FlowLayout.CardOf(flow);
        var gone = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (flow.FindNode(id) is not { } node)
                continue;
            gone.Add(id);
            if (node is GroupNode card)
                gone.UnionWith(card.Members.Where(m => cardOf.GetValueOrDefault(m) == id));
        }
        foreach (var card in flow.Nodes.OfType<GroupNode>())
        {
            if (gone.Count > 0 && card.Members.Where(m => cardOf.GetValueOrDefault(m) == card.Id).All(gone.Contains))
                gone.Add(card.Id);
        }
        return gone;
    }

    public static Flow Delete(Flow flow, IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(ids);
        var cardOf = FlowLayout.CardOf(flow);
        var gone = DeletedBy(flow, ids);
        if (gone.Count == 0)
            return flow;

        // What a deleted node waited for: its own edges, a member its card's.
        ValueList<Edge> Waits(string id) =>
            cardOf.TryGetValue(id, out var card) ? flow.FindNode(card)!.After : flow.FindNode(id)?.After ?? [];

        // Memoized per deleted node, deduplicated as it goes: split/join layers would otherwise multiply paths.
        var resolved = new Dictionary<string, List<Edge>>(StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        IEnumerable<Edge> Bridge(Edge edge)
        {
            if (!gone.Contains(edge.From))
                return [edge];
            if (edge.When == EdgeWhen.Failure || !visiting.Add(edge.From)) // a loop can't bridge to anything
                return [];
            if (!resolved.TryGetValue(edge.From, out var bridged))
            {
                bridged = [.. Waits(edge.From).SelectMany(Bridge).DistinctBy(e => e.From, StringComparer.Ordinal)];
                resolved[edge.From] = bridged;
            }
            visiting.Remove(edge.From);
            return bridged;
        }

        var result = flow with
        {
            Nodes = [.. flow.Nodes.Where(n => !gone.Contains(n.Id)).Select(n => n switch
            {
                GroupNode g => g with
                {
                    Members = [.. g.Members.Where(m => !gone.Contains(m))],
                    After = Merge(g.After.SelectMany(Bridge)),
                },
                _ => n with { After = Merge(n.After.SelectMany(Bridge)) },
            })],
        };
        foreach (var card in result.Nodes.OfType<GroupNode>().Where(g => g.Members.Count == 1).ToList())
            result = FlowEdits.Ungroup(result, card.Id).Flow; // one step: never refused
        return StepValues.DropRefsTo(result, gone); // values of deleted steps: their fallback text (D-129)
    }

    /// <summary>
    /// Ctrl+C: the selected steps and cards (a card with its members). A member copied without its card leaves it as
    /// it would when dragged out: with the card's edges and failure handling.
    /// </summary>
    public static FlowClip Copy(Flow flow, IEnumerable<string> ids)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(ids);
        var cardOf = FlowLayout.CardOf(flow);
        var picked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (flow.FindNode(id) is not { } node)
                continue;
            picked.Add(id);
            if (node is GroupNode card)
                picked.UnionWith(card.Members.Where(m => cardOf.GetValueOrDefault(m) == id));
        }
        var nodes = new List<Node>();
        foreach (var node in flow.Nodes.Where(n => picked.Contains(n.Id)))
        {
            if (node is GroupNode g)
                nodes.Add(g with { Members = [.. g.Members.Where(picked.Contains)] });
            else if (cardOf.TryGetValue(node.Id, out var cardId) && !picked.Contains(cardId) && flow.FindNode(cardId) is GroupNode card)
                nodes.Add((node.OnFailure == FailurePolicy.Ignore ? node : node with { OnFailure = card.OnFailure }) with { After = card.After });
            else
                nodes.Add(node);
        }
        return new FlowClip([.. nodes]);
    }

    /// <summary>
    /// Ctrl+V / Ctrl+D: the clip's steps under new ids, appended to the flow, with their edges among themselves. With
    /// <paramref name="after"/> (the selection on paste) the first pasted steps run after it, in the next column;
    /// without (duplicate) they keep the edges from steps the flow still has, so they land beside the originals.
    /// Returns the pasted top-level ids.
    /// </summary>
    public static (Flow Flow, ValueList<string> Ids) Paste(Flow flow, FlowClip clip, IEnumerable<string>? after)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(clip);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in clip.Nodes)
        {
            if (!map.ContainsKey(node.Id))
                map[node.Id] = Unique(BaseOf(node.Id), id => flow.FindNode(id) is not null || map.ContainsValue(id));
        }
        var anchors = after is null ? null : Anchors(flow, after);
        var cardOf = FlowLayout.CardOf(flow);
        var members = clip.Nodes.OfType<GroupNode>().SelectMany(g => g.Members).ToHashSet(StringComparer.Ordinal);
        var pasted = new List<Node>();
        foreach (var node in clip.Nodes.DistinctBy(n => n.Id))
        {
            var inner = node.After.Where(e => map.ContainsKey(e.From)).Select(e => e with { From = map[e.From] }).ToList();
            ValueList<Edge> edges = members.Contains(node.Id) ? []
                : anchors is not null ? (inner.Count > 0 ? [.. inner] : [.. anchors.Select(a => new Edge(a))])
                : Merge(node.After.Where(e => map.ContainsKey(e.From) || flow.FindNode(e.From) is not null)
                    // A source grouped since the copy: wait for its card (edges never name a member).
                    .Select(e => e with { From = map.TryGetValue(e.From, out var to) ? to : cardOf.GetValueOrDefault(e.From, e.From) }));
            pasted.Add(node switch
            {
                GroupNode g => g with { Id = map[g.Id], Members = [.. g.Members.Where(map.ContainsKey).Select(m => map[m])], After = edges },
                StepNode s => s with { Id = map[s.Id], After = edges },
                _ => node,
            });
        }
        // A pasted step reads the copy of a step it was pasted with; a value of a step outside the clip stays and is checked again.
        var pastedIds = pasted.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);
        var next = flow with { Nodes = [.. flow.Nodes, .. pasted] };
        next = StepValues.RewriteRefs(next, (step, r) =>
            pastedIds.Contains(step.Id) && r.Source == ValueSource.Step && map.TryGetValue(r.Name, out var to) && to != r.Name
                ? (r with { Name = to }).ToString() : null);
        return (next,
            [.. clip.Nodes.Where(n => !members.Contains(n.Id)).Select(n => map[n.Id]).Distinct()]);
    }

    /// <summary>The top-level ids (a member's card) of the existing nodes among <paramref name="ids"/>, in flow order.</summary>
    private static List<string> Anchors(Flow flow, IEnumerable<string> ids)
    {
        var cardOf = FlowLayout.CardOf(flow);
        var tops = ids.Where(id => flow.FindNode(id) is not null).Select(id => cardOf.GetValueOrDefault(id, id)).ToHashSet(StringComparer.Ordinal);
        return [.. flow.Nodes.Select(n => n.Id).Where(tops.Contains).Distinct()];
    }

    private static ValueList<Edge> Merge(IEnumerable<Edge> edges) => [.. edges.DistinctBy(e => e.From, StringComparer.Ordinal)];

    /// <summary>"bt-2" → "bt", so copies of copies count on ("bt-3") instead of growing ("bt-2-2").</summary>
    private static string BaseOf(string id) => Suffix().Replace(id, "") is { Length: > 0 } stem ? stem : id;

    private static string Unique(string stem, Func<string, bool> taken)
    {
        var id = stem;
        for (var n = 2; taken(id); n++)
            id = string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}");
        return id;
    }

    [GeneratedRegex(@"-\d+$")]
    private static partial Regex Suffix();
}
