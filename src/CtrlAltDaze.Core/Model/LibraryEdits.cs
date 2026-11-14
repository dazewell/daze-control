using CtrlAltDaze.Core.Catalog;

namespace CtrlAltDaze.Core.Model;

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
        if (config.Library.Contains(kind, newKey) || KeyClashes(config.Library, kind, newKey))
            return (config, RenameRefusal.Taken);

        var library = config.Library;
        library = kind switch
        {
            LibraryKind.Audio => library with
            {
                Audio = [.. library.Audio.Select(d => d.Key == key ? d with { Key = newKey } : d)],
                Bound = [.. library.Bound.Select(b => b.Audio == key ? b with { Audio = newKey } : b)],
            },
            LibraryKind.Bluetooth => library with
            {
                Bluetooth = [.. library.Bluetooth.Select(d => d.Key == key ? d with { Key = newKey } : d)],
                Bound = [.. library.Bound.Select(b => b.Bluetooth == key ? b with { Bluetooth = newKey } : b)],
            },
            LibraryKind.Bound => library with { Bound = [.. library.Bound.Select(d => d.Key == key ? d with { Key = newKey } : d)] },
            LibraryKind.Display => library with { Displays = [.. library.Displays.Select(d => d.Key == key ? d with { Key = newKey } : d)] },
            _ => library with { Android = [.. library.Android.Select(d => d.Key == key ? d with { Key = newKey } : d)] },
        };
        var flows = config.Flows.Select(f => Uses(f, config, kind, key)
            ? f with
            {
                Inputs = new(f.Inputs.Select(i => Refers(kind, i.Value.Kind) && i.Value.Default == key
                    ? KeyValuePair.Create(i.Key, i.Value with { Default = newKey })
                    : i)),
                Nodes = [.. f.Nodes.Select(n => n is StepNode s ? RenamedPassed(Renamed(s, kind, key, newKey), config, kind, key, newKey) : n)],
            }
            : f);
        return (config with { Library = library, Flows = [.. flows] }, RenameRefusal.None);
    }

    /// <summary>
    /// Bluetooth and bound devices: a new display name, the key (what steps and files name) staying put, so nothing
    /// else changes. The name must be new among that kind's names; typing the key back clears the name.
    /// </summary>
    public static (Configuration Config, RenameRefusal Refusal) Retitle(Configuration config, LibraryKind kind, string key, string name)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(name);
        name = name.Trim();
        var library = config.Library;
        if (kind is not (LibraryKind.Bluetooth or LibraryKind.Bound) || !library.Contains(kind, key))
            return (config, RenameRefusal.Missing);
        if (name.Length == 0)
            return (config, RenameRefusal.Empty);
        var others = kind == LibraryKind.Bluetooth
            ? library.Bluetooth.Where(d => d.Key != key).Select(d => d.Name ?? d.Key)
            : library.Bound.Where(d => d.Key != key).Select(d => d.Name ?? d.Key);
        if (others.Contains(name, StringComparer.OrdinalIgnoreCase))
            return (config, RenameRefusal.Taken);
        var stored = name == key ? null : name;
        library = kind == LibraryKind.Bluetooth
            ? library with { Bluetooth = [.. library.Bluetooth.Select(d => d.Key == key ? d with { Name = stored } : d)] }
            : library with { Bound = [.. library.Bound.Select(d => d.Key == key ? d with { Name = stored } : d)] };
        return (config with { Library = library }, RenameRefusal.None);
    }

    /// <summary>
    /// Whether <paramref name="flow"/> uses this Library item: a step setting names it, an input's default is it, or a
    /// <c>flow.run</c> passes it to an input of that kind (D-064). "Used by" counts these; Remove is off while any.
    /// </summary>
    public static bool Uses(Flow flow, Configuration config, LibraryKind kind, string key)
    {
        ArgumentNullException.ThrowIfNull(flow);
        ArgumentNullException.ThrowIfNull(config);
        return flow.Inputs.Values.Any(i => Refers(kind, i.Kind) && i.Default == key)
            || flow.Nodes.OfType<StepNode>().Any(s => Names(s, kind, key) is not null || Passed(s, config, kind).Any(e => e.Value == key));
    }

    /// <summary>The values a <c>flow.run</c> step passes to inputs of this kind of the flow it calls.</summary>
    private static IEnumerable<KeyValuePair<string, string>> Passed(StepNode step, Configuration config, LibraryKind kind)
    {
        if (step.Step != ProviderCatalog.RunFlowId
            || Validation.ConfigValidator.CalledFlow(step) is not { } target
            || config.Flows.FirstOrDefault(f => f.Id == target) is not { } called)
            return [];
        return FlowInputs.Given(step).Where(e => called.Inputs.TryGetValue(e.Key, out var input) && Refers(kind, input.Kind));
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
        return Holders(step, kind).FirstOrDefault(name => step.With.TryGetValue(name, out var value) && value is ScalarParam { Text: var text } && text == key);
    }

    /// <summary>
    /// The settings of <paramref name="step"/> that can hold a key of this kind: its Library settings, and a Pick device's
    /// <c>default</c> when that pick offers the kind (D-128).
    /// </summary>
    private static IEnumerable<string> Holders(StepNode step, LibraryKind kind)
    {
        if (ProviderCatalog.Find(step.Step) is not { } descriptor)
            return [];
        var pickKind = PickResults.IsPick(step) ? PickResults.KindOf(step) : null;
        return descriptor.Params
            .Where(p => p.Kind == ParamKind.LibraryRef && p.Library is { } k && Refers(kind, k)
                || p.LibraryFrom is not null && pickKind is { } picked && Refers(kind, picked))
            .Select(p => p.Name);
    }

    private static StepNode Renamed(StepNode step, LibraryKind kind, string key, string newKey)
    {
        if (Names(step, kind, key) is null)
            return step;
        var refs = Holders(step, kind).ToHashSet(StringComparer.Ordinal);
        return step with
        {
            With = new(step.With.Select(e => refs.Contains(e.Key) && e.Value is ScalarParam { Text: var text } && text == key
                ? KeyValuePair.Create(e.Key, (ParamValue)newKey)
                : e)),
        };
    }

    /// <summary>
    /// Whether a setting or input of kind <paramref name="refKind"/> can hold a <paramref name="kind"/> key: its own kind,
    /// and a bound device's key in Bluetooth and audio settings (D-116).
    /// </summary>
    /// <summary>As <see cref="Refers(LibraryKind, LibraryKind)"/>; a text input (null kind, T23.7) refers to no Library item.</summary>
    public static bool Refers(LibraryKind kind, LibraryKind? refKind) => refKind is { } r && Refers(kind, r);

    public static bool Refers(LibraryKind kind, LibraryKind refKind) =>
        refKind == kind || (kind == LibraryKind.Bound && refKind is LibraryKind.Audio or LibraryKind.Bluetooth);

    /// <summary>Bound devices share the key space of Bluetooth and audio devices: a step setting holds either (D-116).</summary>
    public static bool KeyClashes(Library library, LibraryKind kind, string key)
    {
        ArgumentNullException.ThrowIfNull(library);
        return kind switch
        {
            LibraryKind.Bound => library.Audio.Any(d => d.Key == key) || library.Bluetooth.Any(d => d.Key == key),
            LibraryKind.Audio or LibraryKind.Bluetooth => library.Bound.Any(d => d.Key == key),
            _ => false,
        };
    }
}
