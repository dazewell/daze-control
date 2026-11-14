using CtrlAltDaze.Core.Catalog;

namespace CtrlAltDaze.Core.Model;

/// <summary>Steps that name another flow by id (<c>flow.run</c>'s <c>flow</c>, any param with <see cref="ConfigRef.Flow"/>) and flow-event triggers (<c>flow:</c>, T26.3).</summary>
public static class FlowRefs
{
    /// <summary>The other flows with a step that names <paramref name="flowId"/>, in YAML order (D-076: Delete is off).</summary>
    public static IReadOnlyList<Flow> Callers(Configuration config, string flowId)
    {
        ArgumentNullException.ThrowIfNull(config);
        return [.. config.Flows.Where(f => f.Id != flowId && Uses(f, flowId))];
    }

    /// <summary>
    /// The configuration with every step that named <paramref name="oldId"/> naming <paramref name="newId"/> (D-075).
    /// The flow itself isn't renamed here.
    /// </summary>
    public static Configuration Rename(Configuration config, string oldId, string newId)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (oldId == newId || !config.Flows.Any(f => Uses(f, oldId)))
            return config;
        return config with
        {
            Flows = [.. config.Flows.Select(f => Uses(f, oldId)
                ? f with
                {
                    Nodes = [.. f.Nodes.Select(n => n is StepNode s && Names(s, oldId) ? Renamed(s, oldId, newId) : n)],
                    Triggers = new([.. f.Triggers.Select(t => t is EventTrigger e && Waits(e, oldId) ? e with { Flow = newId } : t)]),
                }
                : f)],
        };
    }

    private static bool Uses(Flow flow, string flowId) =>
        flow.Nodes.OfType<StepNode>().Any(s => Names(s, flowId)) || flow.Triggers.OfType<EventTrigger>().Any(e => Waits(e, flowId));

    /// <summary>A flow-event trigger's <c>flow:</c> names the flow, trimmed as the reader and the runtime read it.</summary>
    private static bool Waits(EventTrigger trigger, string flowId) =>
        TriggerText.IsFlowEvent(trigger.Name) && TriggerText.FlowFilter(trigger.Flow) == flowId;

    private static IEnumerable<string> FlowParams(StepNode step) =>
        ProviderCatalog.Find(step.Step)?.Params.Where(p => p.RefersTo == ConfigRef.Flow).Select(p => p.Name) ?? [];

    private static bool Names(StepNode step, string flowId) =>
        FlowParams(step).Any(p => step.With.TryGetValue(p, out var value) && Mentions(value, flowId)); // as ConfigValidator.CalledFlow

    /// <summary>A flow id written as a scalar, or as an item of a list (<c>menu.show</c>'s <c>items</c>).</summary>
    private static bool Mentions(ParamValue value, string flowId) => value switch
    {
        ScalarParam { Text: var text } => text.Trim() == flowId,
        ListParam list => list.Items.Any(i => Mentions(i, flowId)),
        _ => false,
    };

    private static ParamValue Replaced(ParamValue value, string oldId, string newId) => value switch
    {
        ScalarParam { Text: var text } when text.Trim() == oldId => newId,
        ListParam list => new ListParam([.. list.Items.Select(i => Replaced(i, oldId, newId))]),
        _ => value,
    };

    private static StepNode Renamed(StepNode step, string oldId, string newId)
    {
        var refs = FlowParams(step).ToHashSet(StringComparer.Ordinal);
        return step with
        {
            With = new(step.With.Select(e => refs.Contains(e.Key)
                ? KeyValuePair.Create(e.Key, Replaced(e.Value, oldId, newId))
                : e)),
        };
    }
}
