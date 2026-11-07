using System.Globalization;
using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Validation;

namespace DazeControl.Core.Backup;

/// <summary>Pure comparison of two loaded configs and the merge for AddMissing (D-103). No I/O.</summary>
internal static class RestoreCompare
{
    public static (List<ItemChange> Items, bool SettingsChanged) Compare(Configuration backup, Configuration current)
    {
        var items = new List<ItemChange>();
        var mine = current.Flows.ToDictionary(f => f.Id);
        foreach (var f in backup.Flows)
            items.Add(!mine.TryGetValue(f.Id, out var c) ? new("flow", f.Id, ChangeState.New)
                : c == f ? new("flow", f.Id, ChangeState.Same) : new("flow", f.Id, ChangeState.Changed, Describe(c, f)));
        var theirs = backup.Flows.Select(f => f.Id).ToHashSet();
        items.AddRange(current.Flows.Where(f => !theirs.Contains(f.Id)).Select(f => new ItemChange("flow", f.Id, ChangeState.OnlyHere)));
        Library("audio", backup.Library.Audio, current.Library.Audio, d => d.Key, items);
        Library("bluetooth", backup.Library.Bluetooth, current.Library.Bluetooth, d => d.Key, items);
        Library("display", backup.Library.Displays, current.Library.Displays, d => d.Key, items);
        Library("android", backup.Library.Android, current.Library.Android, d => d.Key, items);
        return (items, backup.Settings != current.Settings);
    }

    private static void Library<T>(string kind, ValueList<T> backup, ValueList<T> current, Func<T, string> key, List<ItemChange> items)
    {
        var mine = current.ToDictionary(key);
        foreach (var b in backup)
            items.Add(!mine.TryGetValue(key(b), out var c) ? new(kind, key(b), ChangeState.New)
                : EqualityComparer<T>.Default.Equals(b, c) ? new(kind, key(b), ChangeState.Same) : new(kind, key(b), ChangeState.Changed));
        var theirs = backup.Select(key).ToHashSet();
        items.AddRange(current.Where(c => !theirs.Contains(key(c))).Select(c => new ItemChange(kind, key(c), ChangeState.OnlyHere)));
    }

    private static string Describe(Flow current, Flow backup)
    {
        var have = new Dictionary<string, Node>();
        foreach (var n in current.Nodes) have.TryAdd(n.Id, n); // a duplicate id is the validator's problem, not a crash here
        var added = backup.Nodes.Count(n => !have.ContainsKey(n.Id));
        var removed = current.Nodes.Count(n => backup.FindNode(n.Id) is null);
        var edited = backup.Nodes.Count(n => have.TryGetValue(n.Id, out var c) && c != n);
        var parts = new List<string>();
        if (added > 0) parts.Add(Plural(added, "step added", "steps added"));
        if (removed > 0) parts.Add(Plural(removed, "step removed", "steps removed"));
        if (edited > 0) parts.Add(Plural(edited, "step edited", "steps edited"));
        return parts.Count > 0 ? string.Join(", ", parts) : "name or settings changed";
    }

    private static string Plural(int n, string one, string many) => n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? one : many);

    /// <summary>Current + what the backup has that the current lacks (flows by id, library by key, step defaults by step id). Nothing is removed or replaced.</summary>
    public static (Configuration Merged, List<string> KeptCurrent) Merge(Configuration backup, Configuration current)
    {
        var kept = new List<string>();
        var haveFlows = current.Flows.ToDictionary(f => f.Id);
        foreach (var f in backup.Flows.Where(f => haveFlows.TryGetValue(f.Id, out var c) && c != f))
            kept.Add($"Flow \"{f.Id}\" exists here, so this PC's version was kept.");
        var lib = new Library
        {
            Audio = Add(current.Library.Audio, backup.Library.Audio, d => d.Key, "audio device", kept),
            Bluetooth = Add(current.Library.Bluetooth, backup.Library.Bluetooth, d => d.Key, "Bluetooth device", kept),
            Displays = Add(current.Library.Displays, backup.Library.Displays, d => d.Key, "screen layout", kept),
            Android = Add(current.Library.Android, backup.Library.Android, d => d.Key, "Android device", kept),
        };
        var defaults = current.Settings.StepDefaults.ToList();
        defaults.AddRange(backup.Settings.StepDefaults.Where(d => !current.Settings.StepDefaults.ContainsKey(d.Key)));
        var merged = current with
        {
            Flows = new ValueList<Flow>(current.Flows.Concat(backup.Flows.Where(f => !haveFlows.ContainsKey(f.Id)))),
            Library = lib,
            Settings = current.Settings with { StepDefaults = new ValueDictionary<StepDefault>(defaults) },
        };
        return (merged, kept);
    }

    private static ValueList<T> Add<T>(ValueList<T> current, ValueList<T> backup, Func<T, string> key, string what, List<string> kept)
    {
        var have = current.ToDictionary(key);
        foreach (var b in backup.Where(b => have.TryGetValue(key(b), out var c) && !EqualityComparer<T>.Default.Equals(b, c)))
            kept.Add($"The {what} \"{key(b)}\" exists here, so this PC's version was kept.");
        return new ValueList<T>(current.Concat(backup.Where(b => !have.ContainsKey(key(b)))));
    }

    /// <summary>Things that restore but won't work as is (DESIGN §8). Read-only; <paramref name="pathExists"/> runs once per distinct path.</summary>
    public static List<string> NeedsAttention(
        Configuration backup, IEnumerable<Problem> problems, string backupMachine, string thisMachine,
        IReadOnlyCollection<string>? savedCredentials, Func<string, bool> pathExists)
    {
        var list = new List<string>();
        foreach (var g in problems.Where(p => p.Severity == Severity.Error && p.FlowId is not null).GroupBy(p => p.FlowId!))
            list.Add($"Flow \"{g.Key}\" won't run: {g.First().Message}");
        var otherPc = !string.Equals(backupMachine, thisMachine, StringComparison.OrdinalIgnoreCase);
        if (otherPc)
        {
            foreach (var d in backup.Library.Bluetooth)
                list.Add($"Pair \"{d.Key}\" in Windows first (Bluetooth & devices); the flows that use it then work.");
            foreach (var d in backup.Library.Displays)
                list.Add($"Screen layout \"{d.Key}\" was captured on {backupMachine}; it needs the same screens here.");
        }
        foreach (var d in backup.Library.Android.Where(d => d.Adb is { Length: > 0 } adb && IsFixed(adb) && !pathExists(Expand(adb))))
            list.Add($"adb for \"{d.Key}\" isn't at {d.Adb}.");
        var steps = backup.Flows.SelectMany(f => f.Nodes).OfType<StepNode>().ToList();
        if (savedCredentials is not null)
            foreach (var name in steps.Select(s => Scalar(s, "credential")).Where(n => n is { Length: > 0 }).Distinct()
                         .Where(n => !savedCredentials.Contains(n!, StringComparer.OrdinalIgnoreCase)))
                list.Add($"Re-save the credential \"{name}\" in Library › Credentials.");
        var missing = steps.SelectMany(PathParams).Where(IsFixed).Select(Expand).Distinct(StringComparer.OrdinalIgnoreCase).Count(p => !pathExists(p));
        if (missing > 0)
            list.Add($"{missing.ToString(CultureInfo.InvariantCulture)} fixed path{(missing == 1 ? "" : "s")} in steps don't exist on this PC. Those steps fail unless the file is at the same place.");
        return list;
    }

    private static string? Scalar(StepNode s, string name) => s.With.TryGetValue(name, out var v) && v is ScalarParam p ? p.Text : null;

    private static IEnumerable<string> PathParams(StepNode step)
    {
        var d = ProviderCatalog.All.FirstOrDefault(x => x.Id == step.Step);
        if (d is null) yield break;
        foreach (var spec in d.Params.Where(p => p.Kind is ParamKind.FilePath or ParamKind.FolderPath
                     || (step.Step.StartsWith("file.", StringComparison.Ordinal) && p.Name == "path")))
            if (Scalar(step, spec.Name) is { Length: > 0 } text)
                yield return text;
    }

    private static string Expand(string p) => Environment.ExpandEnvironmentVariables(p);

    // Templates and flow inputs ($x, {…}) are not paths yet.
    private static bool IsFixed(string p) =>
        !p.Contains('$', StringComparison.Ordinal) && !p.Contains('{', StringComparison.Ordinal) && Path.IsPathFullyQualified(Expand(p));
}
