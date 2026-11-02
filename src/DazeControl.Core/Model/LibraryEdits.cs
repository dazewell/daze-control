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
        var flows = config.Flows.Select(f => f.Nodes.Any(n => n is StepNode s && Names(s, kind, key) is not null)
            ? f with { Nodes = [.. f.Nodes.Select(n => n is StepNode s ? Renamed(s, kind, key, newKey) : n)] }
            : f);
        return (config with { Library = library, Flows = [.. flows] }, RenameRefusal.None);
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
