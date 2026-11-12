using System.Globalization;
using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Inspector;

namespace DazeControl.Editor.ViewModels.LibraryPage;

/// <summary>
/// Library › "Add by hand" (D-123): an audio, Bluetooth or Android device typed in rather than picked from what Windows
/// sees, so flows can use a device that is off, out of range or not paired yet. It is stored exactly like a picked one,
/// so a device that turns up later matches it (address / name text) instead of being offered again.
/// </summary>
public static class ManualItems
{
    /// <summary>The kinds that can be typed in: a screen layout is captured, a bound device is bound, programs come from flows.</summary>
    public static bool CanAdd(LibraryKind kind) => kind is LibraryKind.Audio or LibraryKind.Bluetooth or LibraryKind.Android;

    /// <summary>
    /// The Library with the new item, or the one sentence saying why not. <paramref name="value"/> is the audio device's
    /// name text, the Bluetooth address or the Android address; <paramref name="name"/> names a Bluetooth or Android device (its key is made from it).
    /// </summary>
    public static (Library? Library, string? Key, string? Error) Add(Library library, LibraryKind kind, string value, string name, AudioFlow flow = AudioFlow.Output)
    {
        value = value.Trim();
        name = name.Trim();
        switch (kind)
        {
            case LibraryKind.Audio:
            {
                if (value.Length == 0)
                    return Refuse("Type the device's name as Windows shows it, or a part of it.");
                if (library.Audio.FirstOrDefault(d => d.Flow == flow && string.Equals(d.Match, value, StringComparison.OrdinalIgnoreCase)) is { } same)
                    return Refuse($"{same.Match} is already in your Library.");
                var key = LibraryDevices.NewKey(library, kind, value);
                return (library with { Audio = [.. library.Audio, new AudioDevice(key, value, flow)] }, key, null);
            }
            case LibraryKind.Bluetooth:
            {
                if (NormalizeBluetooth(value) is not { } address)
                    return Refuse(value.Length == 0 ? "Type the device's Bluetooth address." : $"\"{value}\" isn't a Bluetooth address (like B8:20:8E:ED:48:FC).");
                if (library.Bluetooth.FirstOrDefault(d => string.Equals(d.Address, address, StringComparison.OrdinalIgnoreCase)) is { } same)
                    return Refuse($"{same.Name ?? same.Key} already has this address.");
                if (name.Length == 0)
                    return Refuse("Give it a name.");
                var key = LibraryDevices.NewKey(library, kind, name);
                return (library with { Bluetooth = [.. library.Bluetooth, new BluetoothDevice(key, address, name == key ? null : name)] }, key, null);
            }
            case LibraryKind.Android:
            {
                if (NormalizeAndroid(value) is not { } address)
                    return Refuse(value.Length == 0 ? "Type the device's network address." : $"\"{value}\" isn't a network address (like 192.168.1.50 or 192.168.1.50:5555).");
                var serial = AndroidDevice.SerialOf(address);
                if (library.Android.FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.OrdinalIgnoreCase)) is { } same)
                    return Refuse($"{same.Key} already has this address.");
                if (name.Length == 0)
                    return Refuse("Give it a name.");
                var key = LibraryDevices.NewKey(library, kind, name);
                return (library with { Android = [.. library.Android, new AndroidDevice(key, address, new([]))] }, key, null);
            }
            default:
                return Refuse($"A {Library.KindName(kind)} can't be added by hand.");
        }
    }

    /// <summary>"b8-20-8e-ed-48-fc", "B8208EED48FC" → "B8:20:8E:ED:48:FC"; null when it isn't six hex bytes.</summary>
    public static string? NormalizeBluetooth(string text)
    {
        var hex = new string([.. text.Where(c => c is not (':' or '-' or ' ' or '.'))]);
        if (hex.Length != 12 || !hex.All(char.IsAsciiHexDigit))
            return null;
        return string.Join(':', Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2).ToUpperInvariant()));
    }

    /// <summary>A host name or IP, optionally ":port" (1–65535); null when it has spaces or a bad port.</summary>
    public static string? NormalizeAndroid(string text)
    {
        if (text.Length == 0 || text.Any(char.IsWhiteSpace))
            return null;
        var colon = text.LastIndexOf(':');
        var host = colon < 0 ? text : text[..colon];
        if (host.Length == 0 || host.Contains(':', StringComparison.Ordinal) || !host.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-'))
            return null;
        if (colon >= 0 && !(int.TryParse(text[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535))
            return null;
        return text;
    }

    private static (Library?, string?, string?) Refuse(string error) => (null, null, error);
}
