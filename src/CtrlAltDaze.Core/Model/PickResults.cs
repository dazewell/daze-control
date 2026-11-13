using CtrlAltDaze.Core.Catalog;

namespace CtrlAltDaze.Core.Model;

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

    /// <summary>
    /// The value a device setting stores to use <paramref name="value"/> of step <paramref name="stepId"/> (D-130):
    /// <c>$result:id</c> for <c>device</c> (D-117's form), else <c>$result:id.value</c>.
    /// </summary>
    public static string Ref(string stepId, string value) =>
        value == StepValues.DeviceName ? Prefix + stepId : Prefix + stepId + "." + value;

    /// <summary>The step a setting names (<c>$result:pick</c> → "pick", <c>$result:set.previous</c> → "set"), or null for any other value.</summary>
    public static string? StepIn(ParamValue value) => RefIn(value)?.Step;

    public static string? StepIn(string text) => RefIn(text)?.Step;

    /// <summary>
    /// The step and value a <c>$result:</c> setting names (D-130): <c>$result:set.previous</c> → ("set", "previous");
    /// <c>$result:pick</c> → ("pick", "device"). Step ids hold no '.', so the first one splits. Null for any other value.
    /// </summary>
    public static (string Step, string Value)? RefIn(ParamValue value) => value is ScalarParam { Text: var text } ? RefIn(text) : null;

    public static (string Step, string Value)? RefIn(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var t = text.Trim();
        if (!t.StartsWith(Prefix, StringComparison.Ordinal) || t.Length == Prefix.Length)
            return null;
        var rest = t[Prefix.Length..];
        var dot = rest.IndexOf('.', StringComparison.Ordinal);
        return dot < 0 ? (rest, StepValues.DeviceName) : (rest[..dot], rest[(dot + 1)..]);
    }

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
    /// Why <paramref name="nodeId"/>'s setting needing <paramref name="kind"/> can't hold <paramref name="text"/>, a
    /// <c>$result:</c> value (D-130), or null. A Pick's <c>device</c> keeps D-117's sentences; any other step's value goes
    /// through <see cref="StepValues.DeviceProblem"/> (a Device of a kind that fits, always finished first). A <c>$result:id</c>
    /// naming no step keeps "There's no Pick step called …".
    /// </summary>
    public static string? ResultProblem(Flow flow, string nodeId, string text, LibraryKind kind,
        IReadOnlyList<ProviderDescriptor>? catalog = null)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (RefIn(text) is not { } named)
            return null;
        var (step, value) = named;
        if (value == StepValues.DeviceName && (flow.FindNode(step) is not StepNode producer || IsPick(producer)))
            return Problem(flow, nodeId, step, kind);
        return StepValues.DeviceProblem(flow, nodeId, step, value, kind, catalog);
    }

    /// <summary>
    /// What a <c>$result:</c> setting stands for, in words: "Result of Pick speakers" for a Pick's device, "Previous default
    /// from Set default device" for another step's value (<paramref name="separator"/> after "of"/"from"); null when it names
    /// nothing in <paramref name="flow"/>.
    /// </summary>
    public static string? Describe(Flow flow, string text, string separator = " ")
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (RefIn(text) is not { Step: var step, Value: var value } || flow.FindNode(step) is not StepNode producer)
            return null;
        if (IsPick(producer) && value == StepValues.DeviceName)
            return "Result of" + separator + NameOf(producer);
        var title = ProviderCatalog.Find(producer.Step) is { } d ? StepValues.Find(d, value)?.Title : null;
        return title is null ? null : title + " from" + separator + StepValues.NameOf(producer);
    }

    /// <summary>
    /// The step with every <c>$result:id</c> in a device setting (or <c>flow.run</c>'s <c>inputs</c>) replaced by its picked
    /// key; other settings keep the text as written. <paramref name="missing"/> names a pick that has no result.
    /// </summary>
    /// <param name="descriptor">The step's type; null = the compiled-in catalog's.</param>
    public static StepNode Resolve(StepNode step, IReadOnlyDictionary<string, string> picked, out string? missing,
        ProviderDescriptor? descriptor = null)
    {
        ArgumentNullException.ThrowIfNull(picked);
        return Resolve(step, (id, value) => value == StepValues.DeviceName && picked.TryGetValue(id, out var key) ? key : null,
            out missing, descriptor);
    }

    /// <summary>
    /// The step with every <c>$result:id[.value]</c> in a device setting (or <c>flow.run</c>'s <c>inputs</c>) replaced by the
    /// Library key <paramref name="device"/> gives for (step id, value name), D-130; other settings keep the text as written.
    /// <paramref name="missing"/> is the first reference with no key, as written after the prefix ("pick", "set.previous").
    /// </summary>
    public static StepNode Resolve(StepNode step, Func<string, string, string?> device, out string? missing,
        ProviderDescriptor? descriptor = null)
    {
        ArgumentNullException.ThrowIfNull(step);
        ArgumentNullException.ThrowIfNull(device);
        descriptor ??= ProviderCatalog.Find(step.Step);
        bool IsDevice(string name) => descriptor?.FindParam(name)?.Kind == ParamKind.LibraryRef;
        bool IsCallInputs(string name) => name == FlowInputs.Param && step.Step == ProviderCatalog.RunFlowId;
        string? lost = null;
        ParamValue Swap(ParamValue value)
        {
            if (RefIn(value) is not { Step: var id, Value: var name })
                return value;
            if (device(id, name) is { } key)
                return new ScalarParam(key);
            lost ??= ((ScalarParam)value).Text.Trim()[Prefix.Length..];
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
