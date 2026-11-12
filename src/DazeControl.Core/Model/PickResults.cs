using DazeControl.Core.Catalog;

namespace DazeControl.Core.Model;

/// <summary>
/// Pick steps (<c>prompt.pick</c>, D-117): the Library item a person picks while the flow runs, and the steps that use it.
/// A device setting (or a value <c>flow.run</c> passes) holds <c>$result:&lt;step id&gt;</c>; the runner swaps in the picked
/// key just before that step runs, so its lock and provider only ever see a Library key (as with inputs, D-064).
/// </summary>
public static class PickResults
{
    public const string Prefix = "$result:";

    /// <summary>The <c>kind</c> setting's words, matching <see cref="Kinds"/>.</summary>
    public static readonly IReadOnlyList<string> KindWords = ["audio", "bluetooth", "display", "android", "bound"];

    private static readonly LibraryKind[] Kinds = [LibraryKind.Audio, LibraryKind.Bluetooth, LibraryKind.Display, LibraryKind.Android, LibraryKind.Bound];

    /// <summary>Whether a pick of <paramref name="picked"/> fills a setting needing <paramref name="setting"/>: the same kind, or a bound device (D-116) in a Bluetooth or audio setting.</summary>
    public static bool Fits(LibraryKind picked, LibraryKind setting) =>
        picked == setting || picked == LibraryKind.Bound && setting is LibraryKind.Audio or LibraryKind.Bluetooth;

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
                return Kinds[i];
        }
        return null;
    }

    /// <summary>The pick steps that always finish before <paramref name="nodeId"/> starts (<see cref="StepValues.Before"/>), in flow order.</summary>
    public static IReadOnlyList<StepNode> Before(Flow flow, string nodeId) => [.. StepValues.Before(flow, nodeId).Where(IsPick)];

    /// <summary>
    /// Why <paramref name="nodeId"/> can't use the result of <paramref name="pickId"/> in a setting needing <paramref name="kind"/>, or null.
    /// </summary>
    public static string? Problem(Flow flow, string nodeId, string pickId, LibraryKind kind)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (flow.FindNode(pickId) is not StepNode pick || !IsPick(pick))
            return $"There's no Pick step called \"{pickId}\" in this flow.";
        if (KindOf(pick) is { } picked && !Fits(picked, kind))
            return $"\"{NameOf(pick)}\" picks {Article(picked)}; this setting needs {Article(kind)}.";
        if (Before(flow, nodeId).All(p => p.Id != pickId))
            return $"\"{NameOf(pick)}\" must run before this step: connect it so this step comes after.";
        return null;
    }

    /// <summary>
    /// The step with every <c>$result:id</c> in a device setting (or <c>flow.run</c>'s <c>inputs</c>) replaced by its picked
    /// key; other settings keep the text as written. <paramref name="missing"/> names a pick that has no result.
    /// </summary>
    /// <param name="descriptor">The step's type; null = the compiled-in catalog's.</param>
    public static StepNode Resolve(StepNode step, IReadOnlyDictionary<string, string> picked, out string? missing,
        ProviderDescriptor? descriptor = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(picked);
        descriptor ??= ProviderCatalog.Find(step.Step);
        bool IsDevice(string name) => descriptor?.FindParam(name)?.Kind == ParamKind.LibraryRef;
        bool IsCallInputs(string name) => name == FlowInputs.Param && step.Step == ProviderCatalog.RunFlowId;
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
        if (!step.With.Any(e => IsDevice(e.Key) && StepIn(e.Value) is not null
            || IsCallInputs(e.Key) && e.Value is MapParam m && m.Entries.Values.Any(v => StepIn(v) is not null)))
        {
            missing = null;
            return step;
        }
        var resolved = step with
        {
            With = new(step.With.Select(e => KeyValuePair.Create(e.Key, e.Value is MapParam map && IsCallInputs(e.Key)
                ? new MapParam(new(map.Entries.Select(m => KeyValuePair.Create(m.Key, Swap(m.Value)))))
                : IsDevice(e.Key) ? Swap(e.Value) : e.Value))),
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

    /// <summary>
    /// The Library key a pick's <c>default</c> stands for (D-128): the text when it is a key of that kind (a bound device
    /// counts for audio and Bluetooth, D-116), else the one item whose name matches (older flows typed a name by hand); null
    /// when nothing, or more than one item, matches.
    /// </summary>
    public static string? DefaultKey(Library library, LibraryKind kind, string? text)
    {
        ArgumentNullException.ThrowIfNull(library);
        if (text?.Trim() is not { Length: > 0 } wanted)
            return null;
        if (library.Contains(kind, wanted) || kind is LibraryKind.Audio or LibraryKind.Bluetooth && library.Contains(LibraryKind.Bound, wanted))
            return wanted;
        IEnumerable<(string Key, string Name)> items = kind switch
        {
            LibraryKind.Bluetooth => library.Bluetooth.Select(d => (d.Key, d.Name ?? d.Key)),
            LibraryKind.Bound => library.Bound.Select(d => (d.Key, d.Name ?? d.Key)),
            _ => [],
        };
        if (kind is LibraryKind.Audio or LibraryKind.Bluetooth)
            items = items.Concat(library.Bound.Where(b => library.Contains(kind, b.Key)).Select(b => (b.Key, b.Name ?? b.Key)));
        var found = items.Where(i => string.Equals(i.Name, wanted, StringComparison.OrdinalIgnoreCase)).Select(i => i.Key).Distinct().ToList();
        return found.Count == 1 ? found[0] : null;
    }

    /// <summary>The step's name on the canvas, else "Pick device".</summary>
    public static string NameOf(StepNode pick) =>
        pick.Name is { Length: > 0 } name ? name : ProviderCatalog.Find(pick.Step)?.Title ?? pick.Id;

    private static string Article(LibraryKind kind) =>
        (kind is LibraryKind.Audio or LibraryKind.Android ? "an " : "a ") + Library.KindName(kind);
}
