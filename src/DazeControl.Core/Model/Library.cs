namespace DazeControl.Core.Model;

/// <summary>Kinds of named, reusable resources a step param can reference (FEATURES F4).</summary>
public enum LibraryKind
{
    Audio,
    Bluetooth,
    Display,
    Android,

    /// <summary>A Bluetooth device and an audio device bound as one (D-116); steps of either kind accept its key.</summary>
    Bound,
}

public enum AudioFlow
{
    Output,
    Input,
}

/// <summary><paramref name="Match"/> is text contained in the endpoint's friendly name.</summary>
public sealed record AudioDevice(string Key, string Match, AudioFlow Flow = AudioFlow.Output);

/// <summary><paramref name="Address"/> as "B8:20:8E:ED:48:FC".</summary>
public sealed record BluetoothDevice(string Key, string Address, string? Name = null);

/// <summary>
/// One logical device made of a Library Bluetooth device and a Library audio device (D-116): a Bluetooth step using its
/// key acts on <paramref name="Bluetooth"/>, an audio step on <paramref name="Audio"/>. Both are Library keys.
/// </summary>
public sealed record BoundDevice(string Key, string Bluetooth, string Audio, string? Name = null);

/// <summary>
/// A captured screen layout (D-019): <paramref name="Monitors"/> are the monitors' device paths, in the order of the
/// paths inside <paramref name="Data"/> (raw CCD paths + modes, base64). Only the engine reads <paramref name="Data"/>.
/// </summary>
public sealed record DisplayLayout(string Key, ValueList<string> Monitors, string Data)
{
    /// <summary>sizeof(DISPLAYCONFIG_PATH_INFO) and sizeof(DISPLAYCONFIG_MODE_INFO) on x64 (a Providers test pins them).</summary>
    public const int PathSize = 72, ModeSize = 64;

    /// <summary>
    /// <paramref name="Data"/> is base64 of [path count][mode count] (int32 each), then the paths, then the modes, and
    /// there is one non-empty monitor per path. Base64 alone isn't enough: a truncated capture still decodes.
    /// </summary>
    public bool IsWellFormed()
    {
        if (Monitors.Count == 0 || Monitors.Any(string.IsNullOrWhiteSpace))
            return false;
        var bytes = new byte[Data.Length];
        if (!Convert.TryFromBase64String(Data, bytes, out var length) || length < sizeof(int) * 2)
            return false;
        var paths = BitConverter.ToInt32(bytes, 0);
        var modes = BitConverter.ToInt32(bytes, sizeof(int));
        return paths == Monitors.Count && modes >= 0 && modes <= 4 * paths
            && length == sizeof(int) * 2 + (long)paths * PathSize + (long)modes * ModeSize;
    }
}

/// <summary>
/// An Android device (Fire TV, Google TV, a phone) reached over adb (network debugging on). <paramref name="Address"/> as "192.168.1.50" or
/// "192.168.1.50:5555"; <paramref name="Adb"/> is adb.exe's path, null = on PATH. Each input is the D-pad moves
/// ("up"/"down") from where the TV's input list opens to that input.
/// </summary>
public sealed record AndroidDevice(string Key, string Address, ValueDictionary<ValueList<string>> Inputs, string? Adb = null)
{
    /// <summary>adb's name for it, "host:port"; 5555 is the port Android's network debugging (adb's default) uses. Also its lock.</summary>
    public string Serial => SerialOf(Address);

    public static string SerialOf(string address) => address.Contains(':', StringComparison.Ordinal) ? address : address + ":5555";
}

/// <summary>Credentials live in Windows Credential Manager, referenced by name only (D-014, D-031).</summary>
public sealed record Library
{
    public static Library Empty { get; } = new();

    public ValueList<AudioDevice> Audio { get; init; } = [];

    public ValueList<BluetoothDevice> Bluetooth { get; init; } = [];

    public ValueList<DisplayLayout> Displays { get; init; } = [];

    public ValueList<AndroidDevice> Android { get; init; } = [];

    public ValueList<BoundDevice> Bound { get; init; } = [];

    /// <summary>What a Library item of this kind is called in messages ("audio device").</summary>
    public static string KindName(LibraryKind kind) => kind switch
    {
        LibraryKind.Audio => "audio device",
        LibraryKind.Bluetooth => "Bluetooth device",
        LibraryKind.Display => "screen layout",
        LibraryKind.Android => "Android device",
        LibraryKind.Bound => "bound device",
        _ => kind.ToString(),
    };

    /// <summary>The one sentence for a missing Library item (T18.4).</summary>
    public static string Missing(LibraryKind kind, string key) => $"There's no {KindName(kind)} called \"{key}\" in your Library.";

    public bool Contains(LibraryKind kind, string key) => kind switch
    {
        LibraryKind.Audio => ResolveAudio(key) is not null,
        LibraryKind.Bluetooth => ResolveBluetooth(key) is not null,
        LibraryKind.Bound => Bound.Any(d => d.Key == key),
        LibraryKind.Display => Displays.Any(d => d.Key == key),
        LibraryKind.Android => Android.Any(d => d.Key == key),
        _ => false,
    };

    /// <summary>The audio device a step's key means: a Library audio device, else the audio side of a bound device.</summary>
    public AudioDevice? ResolveAudio(string key) =>
        Audio.FirstOrDefault(d => d.Key == key)
        ?? (Bound.FirstOrDefault(b => b.Key == key) is { } bound ? Audio.FirstOrDefault(d => d.Key == bound.Audio) : null);

    /// <summary>The Bluetooth device a step's key means: a Library Bluetooth device, else the Bluetooth side of a bound device.</summary>
    public BluetoothDevice? ResolveBluetooth(string key) =>
        Bluetooth.FirstOrDefault(d => d.Key == key)
        ?? (Bound.FirstOrDefault(b => b.Key == key) is { } bound ? Bluetooth.FirstOrDefault(d => d.Key == bound.Bluetooth) : null);

    /// <summary>The bound devices a Bluetooth (<paramref name="kind"/>) or audio Library item is a side of.</summary>
    public IEnumerable<BoundDevice> BoundWith(LibraryKind kind, string key) => Bound.Where(b =>
        kind == LibraryKind.Bluetooth ? b.Bluetooth == key : kind == LibraryKind.Audio && b.Audio == key);
}
