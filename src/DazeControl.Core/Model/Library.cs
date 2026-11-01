namespace DazeControl.Core.Model;

/// <summary>Kinds of named, reusable resources a step param can reference (FEATURES F4).</summary>
public enum LibraryKind
{
    Audio,
    Bluetooth,
    Display,
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

/// <summary>More kinds (Fire TV, programs, credentials) arrive with their providers in M4.</summary>
public sealed record Library
{
    public static Library Empty { get; } = new();

    public ValueList<AudioDevice> Audio { get; init; } = [];

    public ValueList<BluetoothDevice> Bluetooth { get; init; } = [];

    public ValueList<DisplayLayout> Displays { get; init; } = [];

    public bool Contains(LibraryKind kind, string key) => kind switch
    {
        LibraryKind.Audio => Audio.Any(d => d.Key == key),
        LibraryKind.Bluetooth => Bluetooth.Any(d => d.Key == key),
        LibraryKind.Display => Displays.Any(d => d.Key == key),
        _ => false,
    };
}
