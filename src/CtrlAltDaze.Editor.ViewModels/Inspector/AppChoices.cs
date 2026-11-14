using CtrlAltDaze.Ipc;

namespace CtrlAltDaze.Editor.ViewModels.Inspector;

/// <summary>One row of the Android app picker: the name to read and the package id to store (D-094).</summary>
public sealed record AppItem(string Name, string Package)
{
    public string AutomationName => $"{Name}, {Package}";

    /// <summary>What the search box shows once a row is chosen.</summary>
    public override string ToString() => Package;
}

/// <summary>The app picker's list: sorted by name, searched by name or package id (D-094).</summary>
public static class AppChoices
{
    /// <summary>Rows beyond this need a narrower search; keeps the drop-down light on a phone with hundreds of apps.</summary>
    public const int MaxRows = 150;

    public static List<AppItem> From(IEnumerable<DeviceInfo> devices) =>
        Sorted(devices.Where(d => (d.Value ?? "").Length > 0).Select(d => new AppItem(d.Name.Length > 0 ? d.Name : d.Value!, d.Value!)));

    public static List<AppItem> Sorted(IEnumerable<AppItem> apps) =>
        [.. apps.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Package, StringComparer.Ordinal)];

    /// <summary>Rows whose name or package contains every word of the query (any case); all of them for an empty query.</summary>
    public static List<AppItem> Match(IReadOnlyList<AppItem> apps, string query)
    {
        var words = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return [.. apps.Where(a => words.All(w => a.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)
            || a.Package.Contains(w, StringComparison.OrdinalIgnoreCase))).Take(MaxRows)];
    }
}
