using System.Globalization;
using DazeControl.Core.Model;
using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Inspector;

public enum DeviceSource
{
    /// <summary>Not required and left empty: "Current default device".</summary>
    None,
    Library,

    /// <summary>Seen live on this PC, not in the Library yet; picking it adds it silently (UX §5.4).</summary>
    Windows,

    /// <summary>The step names a key the Library doesn't have; stays selected so nothing changes by itself.</summary>
    Missing,

    /// <summary>Displays: capture the screens as they are now into a new layout.</summary>
    Capture,
}

/// <summary>One row of a device picker: glyph + name + caption state (UX §5.4).</summary>
public sealed record DeviceOption(DeviceSource Source, string? Key, string Title, string Caption, string Glyph)
{
    /// <summary>Windows: the live device it came from.</summary>
    public DeviceInfo? Device { get; init; }

    public bool HasCaption => Caption.Length > 0;

    public string AutomationName => HasCaption ? $"{Title}, {Caption}" : Title;
}

/// <summary>Library items next to what the engine sees on this PC (pipe <c>devices</c>): shared by the inspector's
/// pickers and the Library page.</summary>
public static class LibraryDevices
{
    public const string NotFound = "Not found on this PC";

    /// <summary>The engine's name for each kind's live list (<c>dctl devices</c>).</summary>
    public static string PipeKind(LibraryKind kind) => kind switch
    {
        LibraryKind.Audio => "audio",
        LibraryKind.Bluetooth => "bluetooth",
        LibraryKind.Display => "displays",
        _ => "adb",
    };

    public static string Glyph(LibraryKind kind) => FlowItemViewModel.DecodeGlyph(kind switch
    {
        LibraryKind.Audio => "E767",
        LibraryKind.Bluetooth => "E702",
        LibraryKind.Display => "E7F4",
        _ => "E8B2",
    });

    /// <summary>The Library's items of a kind as (key, title).</summary>
    public static IEnumerable<(string Key, string Title)> Items(Library library, LibraryKind kind) => kind switch
    {
        LibraryKind.Audio => library.Audio.Select(d => (d.Key, d.Match)),
        LibraryKind.Bluetooth => library.Bluetooth.Select(d => (d.Key, d.Name ?? d.Key)),
        LibraryKind.Display => library.Displays.Select(d => (d.Key, d.Key)),
        _ => library.Android.Select(d => (d.Key, d.Key)),
    };

    /// <summary>The live device a Library item is, or null.</summary>
    public static DeviceInfo? Match(Library library, LibraryKind kind, string key, IReadOnlyList<DeviceInfo> live) => kind switch
    {
        LibraryKind.Audio when library.Audio.FirstOrDefault(d => d.Key == key) is { } audio => live.FirstOrDefault(d =>
            d.Name.Contains(audio.Match, StringComparison.OrdinalIgnoreCase)
            && (d.Detail ?? "").StartsWith(audio.Flow == AudioFlow.Input ? "Input" : "Output", StringComparison.Ordinal)),
        LibraryKind.Bluetooth when library.Bluetooth.FirstOrDefault(d => d.Key == key) is { } bt =>
            live.FirstOrDefault(d => string.Equals(d.Name, bt.Address, StringComparison.OrdinalIgnoreCase)),
        LibraryKind.Android when library.Android.FirstOrDefault(d => d.Key == key) is { } tv =>
            live.FirstOrDefault(d => string.Equals(d.Name, tv.Serial, StringComparison.OrdinalIgnoreCase)),
        _ => null,
    };

    /// <summary>"Connected", "Output, default", "Not found on this PC"; "" while the live list is unknown.</summary>
    public static string State(Library library, LibraryKind kind, string key, IReadOnlyList<DeviceInfo>? live)
    {
        if (kind == LibraryKind.Display)
        {
            var count = library.Displays.FirstOrDefault(d => d.Key == key)?.Monitors.Count ?? 0;
            return count == 1 ? "1 screen" : string.Create(CultureInfo.InvariantCulture, $"{count} screens");
        }
        if (live is null)
            return "";
        return Match(library, kind, key, live) is { } device ? Describe(kind, device) : NotFound;
    }

    /// <summary>A live device's state in sentence case, without the name Bluetooth puts first.</summary>
    public static string Describe(LibraryKind kind, DeviceInfo device)
    {
        var detail = device.Detail ?? "";
        if (kind == LibraryKind.Bluetooth && detail.LastIndexOf(", ", StringComparison.Ordinal) is >= 0 and var comma)
            detail = detail[(comma + 2)..];
        return detail.Length == 0 ? "" : char.ToUpperInvariant(detail[0]) + detail[1..];
    }

    /// <summary>What to call a live device: Bluetooth lists the address as its name and the friendly name in the detail.</summary>
    public static string Title(LibraryKind kind, DeviceInfo device)
    {
        var detail = device.Detail ?? "";
        return kind == LibraryKind.Bluetooth && detail.LastIndexOf(", ", StringComparison.Ordinal) is > 0 and var comma
            ? detail[..comma]
            : device.Name;
    }

    /// <summary>The picker's rows: none (optional), Library items, a missing key, then this PC's other devices.</summary>
    public static List<DeviceOption> Options(LibraryKind kind, Library library, IReadOnlyList<DeviceInfo>? live, string? current,
        bool required, bool canCapture)
    {
        var glyph = Glyph(kind);
        var options = new List<DeviceOption>();
        if (!required)
            options.Add(new(DeviceSource.None, null, "Current default device", "", glyph));
        foreach (var (key, title) in Items(library, kind))
            options.Add(new(DeviceSource.Library, key, title, State(library, kind, key, live), glyph));
        if (!string.IsNullOrEmpty(current) && !library.Contains(kind, current))
            options.Add(new(DeviceSource.Missing, current, current, NotFound, glyph));
        if (live is not null && kind != LibraryKind.Display)
        {
            foreach (var device in live)
            {
                if (Items(library, kind).Any(i => Match(library, kind, i.Key, [device]) is not null))
                    continue;
                var caption = Describe(kind, device);
                options.Add(new(DeviceSource.Windows, null, Title(kind, device),
                    caption.Length > 0 ? "From Windows · " + caption : "From Windows", glyph) { Device = device });
            }
        }
        if (kind == LibraryKind.Display && canCapture)
            options.Add(new(DeviceSource.Capture, null, "Capture current layout", "Saves how your screens are set up now.", glyph));
        return options;
    }

    /// <summary>The Library with a live device added under a new key made from its name.</summary>
    public static (Library Library, string Key) Add(Library library, LibraryKind kind, DeviceInfo device)
    {
        var title = Title(kind, device);
        var key = NewKey(library, kind, title);
        return kind switch
        {
            LibraryKind.Audio => (library with
            {
                Audio = [.. library.Audio, new AudioDevice(key, device.Name,
                    (device.Detail ?? "").StartsWith("Input", StringComparison.Ordinal) ? AudioFlow.Input : AudioFlow.Output)],
            }, key),
            LibraryKind.Bluetooth => (library with
            {
                Bluetooth = [.. library.Bluetooth, new BluetoothDevice(key, device.Name, title == device.Name ? null : title)],
            }, key),
            LibraryKind.Android => (library with { Android = [.. library.Android, new AndroidDevice(key, device.Name, new([]))] }, key),
            _ => (library, key),
        };
    }

    /// <summary>"technics", "technics-2": lower case, words joined by '-', unique in the kind.</summary>
    public static string NewKey(Library library, LibraryKind kind, string name)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var stem = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (stem.Length == 0)
            stem = "device";
        var key = stem;
        for (var n = 2; library.Contains(kind, key); n++)
            key = string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}");
        return key;
    }
}
