namespace DazeControl.Core.Model;

/// <summary>Flow inputs (D-064): <c>$name</c> in step settings, the values a <c>flow.run</c> passes, and resolving them.</summary>
public static class FlowInputs
{
    /// <summary><c>flow.run</c>'s map of input name → Library key or the caller's own <c>$name</c>.</summary>
    public const string Param = "inputs";

    /// <summary>The input a setting names (<c>$output</c> → "output"), or null for a plain value.</summary>
    public static string? NameIn(ParamValue value) =>
        value is ScalarParam { Text: var text } && text.Trim() is ['$', .. var name] ? name : null;

    /// <summary>What a <c>flow.run</c> step passes: input name → value as written (keys or <c>$name</c>).</summary>
    public static IReadOnlyDictionary<string, string> Given(StepNode call)
    {
        ArgumentNullException.ThrowIfNull(call);
        return call.With.TryGetValue(Param, out var value) && value is MapParam map
            ? map.Entries.Where(e => e.Value is ScalarParam).ToDictionary(e => e.Key, e => ((ScalarParam)e.Value).Text.Trim(), StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>
    /// The flow with every <c>$name</c> of its inputs replaced by the caller's value, else the input's default: in plain
    /// step settings (validation allows them only in device settings) and in the <c>inputs</c> its own <c>flow.run</c>
    /// steps pass (pass-through), so the steps, their locks and nested calls see plain Library keys. Lists and other
    /// maps are left alone. Unknown names stay as written (validation reports them). No inputs: the same instance.
    /// </summary>
    /// <param name="keepAsked">An <c>ask: true</c> input (D-107) nobody answered stays <c>$name</c> for the launcher's prompt.</param>
    public static Flow Apply(Flow flow, IReadOnlyDictionary<string, string> given, bool keepAsked = false)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(given);
        if (flow.Inputs.Count == 0)
            return flow;
        ParamValue Resolve(ParamValue value) => NameIn(value) is { } name && flow.Inputs.TryGetValue(name, out var input)
            ? given.TryGetValue(name, out var key) ? key : keepAsked && input.Ask ? value : input.Default
            : value;
        ParamValue Setting(StepNode step, string param, ParamValue value) =>
            value is MapParam map && param == Param && step.Step == Catalog.ProviderCatalog.RunFlowId
                ? new MapParam(new(map.Entries.Select(e => KeyValuePair.Create(e.Key, Resolve(e.Value)))))
                : Resolve(value);
        return flow with
        {
            Nodes = [.. flow.Nodes.Select(n => n is StepNode step
                ? step with { With = new(step.With.Select(e => KeyValuePair.Create(e.Key, Setting(step, e.Key, e.Value)))) }
                : n)],
        };
    }

    /// <summary>
    /// Why the values a caller passes can't run <paramref name="called"/> with <paramref name="library"/> (the
    /// library its runnable version was validated with), or null. An input it no longer has fails too: under D-021 a
    /// caller's last valid version can meet a newer called flow, and its device must not silently become the default.
    /// </summary>
    /// <param name="callerName">For the message: "Output (from Headphones: sony) isn't in your Library anymore."</param>
    public static string? Check(Flow called, Library library, IReadOnlyDictionary<string, string> given, string callerName)
    {
        ArgumentNullException.ThrowIfNull(called);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(given);
        foreach (var (name, key) in given)
        {
            if (!called.Inputs.TryGetValue(name, out var input))
                return $"{called.Name} has no input called \"{name}\" anymore.";
            if (!library.Contains(input.Kind, key))
                return $"{callerName} passes {input.Label} \"{key}\", but there's no {Library.KindName(input.Kind)} called that in your Library.";
        }
        return null;
    }

    /// <summary>Whether a step setting, or a value its <c>flow.run</c> steps pass, names <c>$name</c>.</summary>
    public static bool IsUsed(Flow flow, string name)
    {
        ArgumentNullException.ThrowIfNull(flow);
        return flow.Nodes.OfType<StepNode>().Any(s => s.With.Any(e => NameIn(e.Value) == name
            || e.Key == Param && e.Value is MapParam map && map.Entries.Values.Any(v => NameIn(v) == name)));
    }

    /// <summary>
    /// The flow with every <c>$name</c> replaced by that input's default (UX §5.4: removing an input, or changing its
    /// kind, keeps the steps doing what they did, #61). The input itself stays; unknown names: the same flow.
    /// </summary>
    public static Flow Detach(Flow flow, string name)
    {
        ArgumentNullException.ThrowIfNull(flow);
        if (!flow.Inputs.TryGetValue(name, out var input))
            return flow;
        ParamValue Resolve(ParamValue value) => NameIn(value) == name ? input.Default : value;
        return flow with
        {
            Nodes = [.. flow.Nodes.Select(n => n is StepNode step && IsUsed(flow with { Nodes = [step] }, name)
                ? step with
                {
                    With = new(step.With.Select(e => KeyValuePair.Create(e.Key, e.Key == Param && e.Value is MapParam map
                        ? new MapParam(new(map.Entries.Select(m => KeyValuePair.Create(m.Key, Resolve(m.Value)))))
                        : Resolve(e.Value)))),
                }
                : n)],
        };
    }

    /// <summary>A new input's name from its kind ("audio", "bluetooth-2"), unique in the flow.</summary>
    public static string NewName(Flow flow, LibraryKind kind)
    {
        ArgumentNullException.ThrowIfNull(flow);
        var stem = kind switch
        {
            LibraryKind.Audio => "audio",
            LibraryKind.Bluetooth => "bluetooth",
            LibraryKind.Display => "layout",
            _ => "android",
        };
        var name = stem;
        for (var n = 2; flow.Inputs.ContainsKey(name); n++)
            name = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{stem}-{n}");
        return name;
    }
}
