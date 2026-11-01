namespace DazeControl.Core.Model;

/// <summary>Kinds of named, reusable resources a step param can reference (FEATURES F4).</summary>
public enum LibraryKind
{
    Audio,
    Bluetooth,
    Display,
    FireTv,
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
public sealed record DisplayLayout(string Key, ValueList<string> Monitors, string Data);

/// <summary>
/// A Fire TV reached over adb (network debugging on). <paramref name="Address"/> as "192.168.1.50" or
/// "192.168.1.50:5555"; <paramref name="Adb"/> is adb.exe's path, null = on PATH. Each input is the D-pad moves
/// ("up"/"down") from where the TV's input list opens to that input.
/// </summary>
public sealed record FireTvDevice(string Key, string Address, ValueDictionary<ValueList<string>> Inputs, string? Adb = null);

/// <summary>Credentials live in Windows Credential Manager, referenced by name only (D-014, D-031).</summary>
public sealed record Library
{
    public static Library Empty { get; } = new();

    public ValueList<AudioDevice> Audio { get; init; } = [];

    public ValueList<BluetoothDevice> Bluetooth { get; init; } = [];

    public ValueList<DisplayLayout> Displays { get; init; } = [];

    public ValueList<FireTvDevice> FireTv { get; init; } = [];

    public bool Contains(LibraryKind kind, string key) => kind switch
    {
        LibraryKind.Audio => Audio.Any(d => d.Key == key),
        LibraryKind.Bluetooth => Bluetooth.Any(d => d.Key == key),
        LibraryKind.Display => Displays.Any(d => d.Key == key),
        LibraryKind.FireTv => FireTv.Any(d => d.Key == key),
        _ => false,
    };
}
