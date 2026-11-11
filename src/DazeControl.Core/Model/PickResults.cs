using DazeControl.Core.Catalog;

namespace DazeControl.Core.Model;

/// <summary>
/// Pick steps (<c>prompt.pick</c>, D-116): the Library item a person picks while the flow runs, and the steps that use it.
/// A device setting (or a value <c>flow.run</c> passes) holds <c>$result:&lt;step id&gt;</c>; the runner swaps in the picked
/// key just before that step runs, so its lock and provider only ever see a Library key (as with inputs, D-064).
/// </summary>
public static class PickResults
{
    public const string Prefix = "$result:";

    /// <summary>The <c>kind</c> setting's words, in <see cref="LibraryKind"/> order.</summary>
    public static readonly IReadOnlyList<string> KindWords = ["audio", "bluetooth", "display", "android"];

    /// <summary>The value a step stores to use a pick step's result.</summary>
    public static string Ref(string pickId) => Prefix + pickId;

    /// <summary>The pick step a setting names (<c>$result:pick</c> → "pick"), or null for any other value.</summary>
    public static string? StepIn(ParamValue value) => value is ScalarParam { Text: var text } ? StepIn(text) : null;

    public static string? StepIn(string text) =>
        text.Trim() is var t && t.StartsWith(Prefix, StringComparison.Ordinal) && t.Length > Prefix.Length ? t[Prefix.Length..] : null;

    public static bool IsPick(Node node) => node is StepNode { Step: ProviderCatalog.PromptPickId };

    /// <summary>What a pick step offers: its <c>kind</c> setting (empty = audio), or null when it names no kind.</summary>
    public static LibraryKind? KindOf(StepNode pick)
    {
        ArgumentNullException.ThrowIfNull(pick);
        var word = pick.With.TryGetValue("kind", out var value) && value is ScalarParam s ? s.Text.Trim() : "";
        if (word.Length == 0)
            return LibraryKind.Audio;
        for (var i = 0; i < KindWords.Count; i++)
        {
            if (KindWords[i] == word)
                return (LibraryKind)i;
        }
        return null;
    }

    /// <summary>
    /// The pick steps that always finish before <paramref name="nodeId"/> starts: ones it waits for, directly or through
    /// other steps and cards (any edge), and the members of cards it waits for. Its own card's members start with it, so
    /// they don't count. In flow order.
    /// </summary>
    public static IReadOnlyList<StepNode> Before(Flow flow, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();
        void Enqueue(Node? node)
        {
            if (node is null)
                return;
            foreach (var edge in node.After)
            {
                if (seen.Add(edge.From))
                    queue.Enqueue(edge.From);
            }
        }
        Enqueue(flow.FindNode(FlowShape.UnitOf(flow, nodeId)));
        while (queue.Count > 0)
        {
            var node = flow.FindNode(queue.Dequeue());
            if (node is GroupNode card)
            {
                foreach (var member in card.Members)
                    seen.Add(member);
            }
            Enqueue(node);
        }
        return [.. flow.Nodes.OfType<StepNode>().Where(s => IsPick(s) && seen.Contains(s.Id))];
    }

    /// <summary>
    /// Why <paramref name="nodeId"/> can't use the result of <paramref name="pickId"/> in a setting needing <paramref name="kind"/>, or null.
    /// </summary>
    public static string? Problem(Flow flow, string nodeId, string pickId, LibraryKind kind)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.FindNode(pickId) is not StepNode pick || !IsPick(pick))
            return $"There's no Pick step called \"{pickId}\" in this flow.";
        if (KindOf(pick) is { } picked && picked != kind)
            return $"\"{NameOf(pick)}\" picks {Article(picked)}; this setting needs {Article(kind)}.";
        if (Before(flow, nodeId).All(p => p.Id != pickId))
            return $"\"{NameOf(pick)}\" must run before this step: connect it so this step comes after.";
        return null;
    }

    /// <summary>The step with every <c>$result:id</c> replaced by its picked key; <paramref name="missing"/> names a pick that has no result.</summary>
    public static StepNode Resolve(StepNode step, IReadOnlyDictionary<string, string> picked, out string? missing)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(picked);
        string? lost = null;
        ParamValue Swap(ParamValue value)
        {
            if (StepIn(value) is not { } id)
                return value;
            if (picked.TryGetValue(id, out var key))
                return new ScalarParam(key);
            lost ??= id;
            return value;
        }
        if (!step.With.Values.Any(v => StepIn(v) is not null || v is MapParam m && m.Entries.Values.Any(e => StepIn(e) is not null)))
        {
            missing = null;
            return step;
        }
        var resolved = step with
        {
            With = new(step.With.Select(e => KeyValuePair.Create(e.Key, e.Value is MapParam map && e.Key == FlowInputs.Param
                ? new MapParam(new(map.Entries.Select(m => KeyValuePair.Create(m.Key, Swap(m.Value)))))
                : Swap(e.Value)))),
        };
        missing = lost;
        return resolved;
    }

    /// <summary>Whether any setting of any step uses <paramref name="pickId"/>'s result.</summary>
    public static bool IsUsed(Flow flow, string pickId)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.Nodes.OfType<StepNode>().Any(s => s.With.Values.Any(v => StepIn(v) == pickId
            || v is MapParam m && m.Entries.Values.Any(e => StepIn(e) == pickId)));
    }

    /// <summary>The step's name on the canvas, else "Pick device".</summary>
    public static string NameOf(StepNode pick) =>
        pick.Name is { Length: > 0 } name ? name : ProviderCatalog.Find(pick.Step)?.Title ?? pick.Id;

    private static string Article(LibraryKind kind) =>
        (kind is LibraryKind.Audio or LibraryKind.Android ? "an " : "a ") + Library.KindName(kind);
}
