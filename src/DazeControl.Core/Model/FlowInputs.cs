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
    /// The flow with every <c>$name</c> of its inputs replaced by the caller's value, else the input's default: in step
    /// settings and in the values its own calls pass (pass-through), so the steps, their locks and nested calls see
    /// plain Library keys. Unknown names stay as written (validation reports them). No inputs: the same instance.
    /// </summary>
    public static Flow Apply(Flow flow, IReadOnlyDictionary<string, string> given)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(given);
        if (flow.Inputs.Count == 0)
            return flow;
        ParamValue Resolve(ParamValue value) => NameIn(value) is { } name && flow.Inputs.TryGetValue(name, out var input)
            ? given.TryGetValue(name, out var key) ? key : input.Default
            : value is MapParam map ? new MapParam(new(map.Entries.Select(e => KeyValuePair.Create(e.Key, Resolve(e.Value)))))
            : value;
        return flow with
        {
            Nodes = [.. flow.Nodes.Select(n => n is StepNode step
                ? step with { With = new(step.With.Select(e => KeyValuePair.Create(e.Key, Resolve(e.Value)))) }
                : n)],
        };
    }

    /// <summary>
    /// Why the values a caller passes can't run <paramref name="called"/> with <paramref name="library"/> (the
    /// library its runnable version was validated with), or null. Unknown input names are left to validation.
    /// </summary>
    /// <param name="callerName">For the message: "Output (from Headphones: sony) isn't in your Library anymore."</param>
    public static string? Check(Flow called, Library library, IReadOnlyDictionary<string, string> given, string callerName)
    {
        ArgumentNullException.ThrowIfNull(called);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(given);
        foreach (var (name, key) in given)
        {
            if (called.Inputs.TryGetValue(name, out var input) && !library.Contains(input.Kind, key))
                return $"{input.Label} (from {callerName}: {key}) isn't in your Library anymore.";
        }
        return null;
    }
}
