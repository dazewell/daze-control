using DazeControl.Core.Catalog;

namespace DazeControl.Core.Model;

public enum RenameRefusal
{
    None,
    Empty,
    Taken,
    Missing,
}

/// <summary>Library edits that reach into the flows, pure so they are tested here.</summary>
public static class LibraryEdits
{
    /// <summary>
    /// Gives a Library item a new key and rewrites every step setting of that kind that named the old one, so the
    /// flows using it keep working. The key is trimmed; it must be new within its kind.
    /// </summary>
    public static (Configuration Config, RenameRefusal Refusal) Rename(Configuration config, LibraryKind kind, string key, string newKey)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(newKey);
        newKey = newKey.Trim();
        if (!config.Library.Contains(kind, key))
            return (config, RenameRefusal.Missing);
        if (newKey.Length == 0)
            return (config, RenameRefusal.Empty);
        if (newKey == key)
            return (config, RenameRefusal.None);
        if (config.Library.Contains(kind, newKey))
            return (config, RenameRefusal.Taken);

        var library = config.Library;
        library = kind switch
        {
            LibraryKind.Audio => library with { Audio = [.. library.Audio.Select(d => d.Key == key ? d with { Key = newKey } : d)] },
            LibraryKind.Bluetooth => library with { Bluetooth = [.. library.Bluetooth.Select(d => d.Key == key ? d with { Key = newKey } : d)] },
            LibraryKind.Display => library with { Displays = [.. library.Displays.Select(d => d.Key == key ? d with { Key = newKey } : d)] },
            _ => library with { Android = [.. library.Android.Select(d => d.Key == key ? d with { Key = newKey } : d)] },
        };
        var flows = config.Flows.Select(f => Uses(f, config, kind, key)
            ? f with
            {
                Inputs = new(f.Inputs.Select(i => i.Value.Kind == kind && i.Value.Default == key
                    ? KeyValuePair.Create(i.Key, i.Value with { Default = newKey })
                    : i)),
                Nodes = [.. f.Nodes.Select(n => n is StepNode s ? RenamedPassed(Renamed(s, kind, key, newKey), config, kind, key, newKey) : n)],
            }
            : f);
        return (config with { Library = library, Flows = [.. flows] }, RenameRefusal.None);
    }

    /// <summary>
    /// Whether <paramref name="flow"/> uses this Library item: a step setting names it, an input's default is it, or a
    /// <c>flow.run</c> passes it to an input of that kind (D-064). "Used by" counts these; Remove is off while any.
    /// </summary>
    public static bool Uses(Flow flow, Configuration config, LibraryKind kind, string key)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(config);
        return flow.Inputs.Values.Any(i => i.Kind == kind && i.Default == key)
            || flow.Nodes.OfType<StepNode>().Any(s => Names(s, kind, key) is not null || Passed(s, config, kind).Any(e => e.Value == key));
    }

    /// <summary>The values a <c>flow.run</c> step passes to inputs of this kind of the flow it calls.</summary>
    private static IEnumerable<KeyValuePair<string, string>> Passed(StepNode step, Configuration config, LibraryKind kind)
    {
        if (step.Step != ProviderCatalog.RunFlowId
            || Validation.ConfigValidator.CalledFlow(step) is not { } target
            || config.Flows.FirstOrDefault(f => f.Id == target) is not { } called)
            return [];
        return FlowInputs.Given(step).Where(e => called.Inputs.TryGetValue(e.Key, out var input) && input.Kind == kind);
    }

    private static StepNode RenamedPassed(StepNode step, Configuration config, LibraryKind kind, string key, string newKey)
    {
        var names = Passed(step, config, kind).Where(e => e.Value == key).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        if (names.Count == 0 || step.With[FlowInputs.Param] is not MapParam map)
            return step;
        var passed = new MapParam(new(map.Entries.Select(e => names.Contains(e.Key) ? KeyValuePair.Create(e.Key, (ParamValue)newKey) : e)));
        return step with { With = new(step.With.Select(e => e.Key == FlowInputs.Param ? KeyValuePair.Create(e.Key, (ParamValue)passed) : e)) };
    }

    /// <summary>The Library setting of <paramref name="step"/> that names <paramref name="key"/> of this kind, or null.</summary>
    public static string? Names(StepNode step, LibraryKind kind, string key)
    {
        ArgumentNullException.ThrowIfNull(step);
        return ProviderCatalog.Find(step.Step)?.Params.FirstOrDefault(p => p.Kind == ParamKind.LibraryRef && p.Library == kind
            && step.With.TryGetValue(p.Name, out var value) && value is ScalarParam { Text: var text } && text == key)?.Name;
    }

    private static StepNode Renamed(StepNode step, LibraryKind kind, string key, string newKey)
    {
        if (Names(step, kind, key) is null)
            return step;
        var refs = ProviderCatalog.Find(step.Step)!.Params
            .Where(p => p.Kind == ParamKind.LibraryRef && p.Library == kind).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
        return step with
        {
            With = new(step.With.Select(e => refs.Contains(e.Key) && e.Value is ScalarParam { Text: var text } && text == key
                ? KeyValuePair.Create(e.Key, (ParamValue)newKey)
                : e)),
        };
    }
}
