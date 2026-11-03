using CommunityToolkit.Mvvm.ComponentModel;
using DazeControl.Core.Layout;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Inspector;

/// <summary>One row of an editable ComboBox (UX §5.4): what it writes, plus a caption. The box shows
/// <see cref="ToString"/>, so picking a row puts its value in the text.</summary>
public sealed record SuggestionItem(string Value, string Caption)
{
    public bool HasCaption => Caption.Length > 0;

    public string AutomationName => HasCaption ? $"{Value}, {Caption}" : Value;

    public override string ToString() => Value;
}

/// <summary>One screen of the "Use only these screens" checklist (D-051).</summary>
public sealed partial class ScreenCheckViewModel(string title, string caption, string value, string? entry, bool plugged, bool on)
    : ObservableObject
{
    internal Action? Toggled;

    public string Title { get; } = title;

    /// <summary>"DP-3 · On now", or #8 for a saved screen that isn't plugged in.</summary>
    public string Caption { get; } = caption;

    /// <summary>What checking it writes: the monitor name, or the port for twins (D-051).</summary>
    public string Value { get; } = value;

    /// <summary>The step's entry that already means this screen, kept as written.</summary>
    public string? Entry { get; } = entry;

    public bool Plugged { get; } = plugged;

    public bool On { get; } = on;

    public string AutomationName => $"{Title}, {Caption}";

    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => Toggled?.Invoke();

    internal void Load(bool isChecked)
    {
        var toggled = Toggled;
        Toggled = null;
        IsChecked = isChecked;
        Toggled = toggled;
    }
}

/// <summary>One key of an ordered list (android.keys): "home, 1 of 3".</summary>
public sealed record KeyTokenViewModel(string Text, int Index, int Count)
{
    public string AutomationName => $"{Text}, {Index + 1} of {Count}";
}

/// <summary>The screens checklist's rows from the live list and the step's entries (D-051).</summary>
public static class ScreenChoices
{
    public const string NotConnected = "Not connected right now. The flow will still try.";

    /// <summary>
    /// Plugged-in screens first, in the engine's order, checked when an entry means them (the provider's rule,
    /// <see cref="ScreenMatch"/>); then entries no screen matches, still checked, with #8.
    /// </summary>
    public static List<ScreenCheckViewModel> Rows(IReadOnlyList<DeviceInfo>? live, IReadOnlyList<string> entries)
    {
        live ??= [];
        var matched = entries.ToDictionary(e => e, e => ScreenMatch.IndexOf(e, live, d => d.Name, Monitor), StringComparer.Ordinal);
        var rows = new List<ScreenCheckViewModel>();
        for (var i = 0; i < live.Count; i++)
        {
            var screen = live[i];
            var monitor = Monitor(screen);
            var on = screen.State == "on";
            var entry = entries.FirstOrDefault(e => matched[e] == i);
            var row = new ScreenCheckViewModel(monitor.Length > 0 ? monitor : screen.Name,
                $"{screen.Name} · {(on ? "On now" : "Off now")}", ValueFor(i, live), entry, plugged: true, on);
            row.Load(entry is not null);
            rows.Add(row);
        }
        foreach (var entry in entries.Where(e => matched[e] < 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var row = new ScreenCheckViewModel(entry, NotConnected, entry, entry, plugged: false, on: false);
            row.Load(true);
            rows.Add(row);
        }
        return rows;
    }

    /// <summary>The list a step gets from the checked rows: entries as written, new ones by <see cref="ValueFor"/>.</summary>
    public static List<string> Entries(IEnumerable<ScreenCheckViewModel> rows) =>
        [.. rows.Where(r => r.IsChecked).Select(r => r.Entry ?? r.Value).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The monitor name, unless it's empty or would mean another screen; then the port (D-051).</summary>
    internal static string ValueFor(int index, IReadOnlyList<DeviceInfo> live)
    {
        var monitor = Monitor(live[index]);
        return monitor.Length > 0 && ScreenMatch.IndexOf(monitor, live, d => d.Name, Monitor) == index
            && live.Count(d => Monitor(d).Equals(monitor, StringComparison.OrdinalIgnoreCase)) == 1
            ? monitor
            : live[index].Name;
    }

    private static string Monitor(DeviceInfo screen) => screen.Value ?? "";
}
