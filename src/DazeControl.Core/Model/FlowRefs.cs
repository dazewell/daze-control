using DazeControl.Core.Catalog;

namespace DazeControl.Core.Model;

/// <summary>Steps that name another flow by id (<c>flow.run</c>'s <c>flow</c>, any param with <see cref="ConfigRef.Flow"/>).</summary>
public static class FlowRefs
{
    /// <summary>The other flows with a step that names <paramref name="flowId"/>, in YAML order (D-076: Delete is off).</summary>
    public static IReadOnlyList<Flow> Callers(Configuration config, string flowId)
    {
        ArgumentNullException.ThrowIfNull(config);
        return [.. config.Flows.Where(f => f.Id != flowId && f.Nodes.OfType<StepNode>().Any(s => Names(s, flowId)))];
    }

    /// <summary>
    /// The configuration with every step that named <paramref name="oldId"/> naming <paramref name="newId"/> (D-075).
    /// The flow itself isn't renamed here.
    /// </summary>
    public static Configuration Rename(Configuration config, string oldId, string newId)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (oldId == newId || !config.Flows.Any(f => f.Nodes.OfType<StepNode>().Any(s => Names(s, oldId))))
            return config;
        return config with
        {
            Flows = [.. config.Flows.Select(f => f.Nodes.OfType<StepNode>().Any(s => Names(s, oldId))
                ? f with { Nodes = [.. f.Nodes.Select(n => n is StepNode s && Names(s, oldId) ? Renamed(s, oldId, newId) : n)] }
                : f)],
        };
    }

    private static IEnumerable<string> FlowParams(StepNode step) =>
        ProviderCatalog.Find(step.Step)?.Params.Where(p => p.RefersTo == ConfigRef.Flow).Select(p => p.Name) ?? [];

    private static bool Names(StepNode step, string flowId) =>
        FlowParams(step).Any(p => step.With.TryGetValue(p, out var value) && value is ScalarParam { Text: var text } && text == flowId);

    private static StepNode Renamed(StepNode step, string oldId, string newId)
    {
        var refs = FlowParams(step).ToHashSet(StringComparer.Ordinal);
        return step with
        {
            With = new(step.With.Select(e => refs.Contains(e.Key) && e.Value is ScalarParam { Text: var text } && text == oldId
                ? KeyValuePair.Create(e.Key, (ParamValue)newId)
                : e)),
        };
    }
}
