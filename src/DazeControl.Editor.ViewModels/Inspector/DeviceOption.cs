using System.Globalization;
using DazeControl.Core.Catalog;
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

    /// <summary>One of this flow's inputs (D-064): stores <c>$name</c>, so a caller can pick the device.
    /// Also an earlier step's device (D-117, D-130): stores <c>$result:id</c> or <c>$result:id.value</c>.</summary>
    Input,

    /// <summary>A group's heading ("This flow's inputs"): shown, never picked.</summary>
    Header,
}

/// <summary>One row of a device picker: glyph + name + caption state (UX §5.4).</summary>
public sealed record DeviceOption(DeviceSource Source, string? Key, string Title, string Caption, string Glyph)
{
    /// <summary>Windows: the live device it came from.</summary>
    public DeviceInfo? Device { get; init; }

    /// <summary>False: shown but can't be picked (a paired Bluetooth device without audio, D-052).</summary>
    public bool IsEnabled { get; init; } = true;

    public bool HasCaption => Caption.Length > 0;

    /// <summary>A value the step can hold: a Library key, a key it already names, or one of the flow's inputs.</summary>
    public bool IsValue => Source is DeviceSource.Library or DeviceSource.Missing or DeviceSource.Input;

    public bool IsHeader => Source == DeviceSource.Header;

    public bool IsRow => !IsHeader;

    public string AutomationName => HasCaption ? $"{Title}, {Caption}" : Title;
}

/// <summary>Library items next to what the engine sees on this PC (pipe <c>devices</c>): shared by the inspector's
/// pickers and the Library page.</summary>
public static class LibraryDevices
{
    public const string NotFound = "Not found on this PC";

    /// <summary>The caption of a bound device in a Bluetooth or audio picker (D-116).</summary>
    public const string BoundCaption = "Bound device";

    /// <summary>The caption of a <c>$name</c> the flow has no input for (the validator's message says the rest).</summary>
    public const string NotAnInput = "Not one of this flow's inputs";

    /// <summary>UX microcopy #26.</summary>
    public const string NotConnectable = "Daze Control can connect only headphones and speakers.";

    /// <summary>The engine's name for each kind's live list (<c>dctl devices</c>).</summary>
    public static string PipeKind(LibraryKind kind) => kind switch
    {
        LibraryKind.Audio => "audio",
        LibraryKind.Bluetooth => "bluetooth",
        LibraryKind.Display => "displays",
        LibraryKind.Bound => "bluetooth",
        _ => "adb",
    };

    public static string Glyph(LibraryKind kind) => FlowItemViewModel.DecodeGlyph(kind switch
    {
        LibraryKind.Audio => "E767",
        LibraryKind.Bluetooth => "E702",
        LibraryKind.Display => "E7F4",
        LibraryKind.Bound => "E71B",
        _ => "E8B2",
    });

    /// <summary>The Library's items of a kind as (key, title).</summary>
    public static IEnumerable<(string Key, string Title)> Items(Library library, LibraryKind kind) => kind switch
    {
        LibraryKind.Audio => library.Audio.Select(d => (d.Key, d.Match)),
        LibraryKind.Bluetooth => library.Bluetooth.Select(d => (d.Key, d.Name ?? d.Key)),
        LibraryKind.Display => library.Displays.Select(d => (d.Key, d.Key)),
        LibraryKind.Bound => library.Bound.Select(d => (d.Key, d.Name ?? d.Key)),
        _ => library.Android.Select(d => (d.Key, d.Key)),
    };

    /// <summary>The live device a Library item is, or null.</summary>
    public static DeviceInfo? Match(Library library, LibraryKind kind, string key, IReadOnlyList<DeviceInfo> live) => kind switch
    {
        LibraryKind.Audio when library.ResolveAudio(key) is { } audio => live.FirstOrDefault(d =>
            d.Name.Contains(audio.Match, StringComparison.OrdinalIgnoreCase)
            && (d.Detail ?? "").StartsWith(audio.Flow == AudioFlow.Input ? "Input" : "Output", StringComparison.Ordinal)),
        LibraryKind.Bluetooth when library.ResolveBluetooth(key) is { } bt =>
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

    /// <summary>A live device's state in sentence case; Bluetooth's is its own field (its detail is the name).</summary>
    public static string Describe(LibraryKind kind, DeviceInfo device)
    {
        var detail = (kind == LibraryKind.Bluetooth ? device.State : device.Detail) ?? "";
        return detail.Length == 0 ? "" : char.ToUpperInvariant(detail[0]) + detail[1..];
    }

    /// <summary>What to call a live device: Bluetooth lists the address as its name and the friendly name in the detail.</summary>
    public static string Title(LibraryKind kind, DeviceInfo device) =>
        kind == LibraryKind.Bluetooth && device.Detail is { Length: > 0 } name ? name : device.Name;

    /// <summary>UX §5.4: the group of a flow's own inputs in its device pickers.</summary>
    public const string InputsGroup = "This flow's inputs";

    /// <summary>D-130 (was D-117's "Picked earlier in this flow"): the group of earlier steps' devices in a step's device pickers.</summary>
    public const string EarlierGroup = "From earlier steps";

    /// <summary>The caption of a <c>$result:</c> that isn't a device of an earlier step that fits (the validator's message says why).</summary>
    public const string NotAnEarlierValue = "Not a device from an earlier step";

    /// <summary>"$result:pick" → "Result of: Pick device" / "Picked when the flow runs".</summary>
    public static DeviceOption PickRow(StepNode pick, LibraryKind kind) =>
        new(DeviceSource.Input, PickResults.Ref(pick.Id), "Result of: " + PickResults.NameOf(pick), "Picked when the flow runs", Glyph(kind));

    /// <summary>
    /// An earlier step's Device value (D-130): a Pick's as <see cref="PickRow"/>, else "$result:set.previous" →
    /// "Previous default from: Set default device" / its help.
    /// </summary>
    public static DeviceOption ValueRow(StepNode step, ValueSpec value, LibraryKind kind) =>
        PickResults.IsPick(step) && value.Name == StepValues.DeviceName ? PickRow(step, kind)
            : new(DeviceSource.Input, PickResults.Ref(step.Id, value.Name), $"{value.Title} from: {StepValues.NameOf(step)}",
                value.Help ?? "Given when the flow runs", Glyph(kind));

    /// <summary>The Library item's name ("Technics"), else the key as written.</summary>
    public static string NameOf(Library library, LibraryKind kind, string key) =>
        Items(library, kind).FirstOrDefault(i => i.Key == key).Title
        ?? library.Bound.FirstOrDefault(b => b.Key == key)?.Name ?? key;

    /// <summary>"$output" → the input's row: "Output" / "Technics unless the caller picks another".</summary>
    public static DeviceOption InputRow(Library library, string name, FlowInput input) =>
        new(DeviceSource.Input, "$" + name, input.Label,
            input.Default.Length == 0 ? "No default yet" : $"{NameOf(library, input.Kind, input.Default)} unless the caller picks another",
            Glyph(input.Kind));

    /// <summary>A heading row of a picker's group.</summary>
    public static DeviceOption Header(string title) => new(DeviceSource.Header, null, title, "", "") { IsEnabled = false };

    /// <summary>
    /// The picker's rows: none (optional), this flow's inputs of the kind under <see cref="InputsGroup"/>, Library
    /// items, a missing key, then this PC's other devices.
    /// </summary>
    public static List<DeviceOption> Options(LibraryKind kind, Library library, IReadOnlyList<DeviceInfo>? live, string? current,
        bool required, bool canCapture, IEnumerable<KeyValuePair<string, FlowInput>>? inputs = null, string inputsGroup = InputsGroup,
        IEnumerable<(StepNode Step, ValueSpec Value)>? earlier = null)
    {
        var glyph = Glyph(kind);
        var options = new List<DeviceOption>();
        if (!required)
            options.Add(new(DeviceSource.None, null, "Current default device", "", glyph));
        // A bound input fits Bluetooth and audio settings too (D-116, D-125).
        var own = (inputs ?? []).Where(i => LibraryEdits.Refers(i.Value.Kind, kind)).ToList();
        if (own.Count > 0)
        {
            options.Add(Header(inputsGroup));
            options.AddRange(own.Select(i => InputRow(library, i.Key, i.Value)));
        }
        // The caller lists only values that fit this kind (StepValues.DevicesBefore).
        var picked = (earlier ?? []).Select(e => ValueRow(e.Step, e.Value, kind)).ToList();
        if (picked.Count > 0)
        {
            options.Add(Header(EarlierGroup));
            options.AddRange(picked);
        }
        if (own.Count > 0 || picked.Count > 0)
            options.Add(Header("Library"));
        foreach (var (key, title) in Items(library, kind))
        {
            // A paired keyboard put in the Library by hand still can't be connected (D-052).
            var notConnectable = live is not null && Match(library, kind, key, live) is { NotConnectable: true };
            options.Add(new(DeviceSource.Library, key, title, notConnectable ? NotConnectable : State(library, kind, key, live), glyph)
            {
                IsEnabled = !notConnectable,
            });
        }
        // A bound device whose side of this kind is in the Library is one of its devices too (D-116).
        if (kind is LibraryKind.Audio or LibraryKind.Bluetooth)
        {
            foreach (var bound in library.Bound.Where(b => library.Contains(kind, b.Key)))
            {
                var state = State(library, kind, bound.Key, live);
                // Its Bluetooth side may be a paired keyboard: it can't be connected either (D-052).
                var notConnectable = live is not null && Match(library, kind, bound.Key, live) is { NotConnectable: true };
                options.Add(new(DeviceSource.Library, bound.Key, bound.Name ?? bound.Key,
                    notConnectable ? NotConnectable : state.Length == 0 ? BoundCaption : $"{BoundCaption} · {state}", Glyph(LibraryKind.Bound))
                {
                    IsEnabled = !notConnectable,
                });
            }
        }
        if (!string.IsNullOrEmpty(current) && !library.Contains(kind, current) && own.All(i => "$" + i.Key != current)
            && picked.All(p => p.Key != current?.Trim()))
        {
            options.Add(new(DeviceSource.Missing, current, current,
                PickResults.StepIn(current) is not null ? NotAnEarlierValue : current.StartsWith('$') ? NotAnInput : NotFound, glyph));
        }
        // A bound device is made in the Library from two items, never added from Windows (D-116).
        if (live is not null && kind is not (LibraryKind.Display or LibraryKind.Bound))
        {
            foreach (var device in live)
            {
                if (Items(library, kind).Any(i => Match(library, kind, i.Key, [device]) is not null))
                    continue;
                var caption = Describe(kind, device);
                options.Add(new(DeviceSource.Windows, null, Title(kind, device),
                    device.NotConnectable ? NotConnectable : caption.Length > 0 ? "From Windows · " + caption : "From Windows", glyph)
                {
                    Device = device,
                    IsEnabled = !device.NotConnectable,
                });
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
        for (var n = 2; library.Contains(kind, key) || LibraryEdits.KeyClashes(library, kind, key); n++)
            key = string.Create(CultureInfo.InvariantCulture, $"{stem}-{n}");
        return key;
    }
}
