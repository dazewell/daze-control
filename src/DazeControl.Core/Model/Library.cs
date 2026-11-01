namespace DazeControl.Core.Model;

/// <summary>Kinds of named, reusable resources a step param can reference (FEATURES F4).</summary>
public enum LibraryKind
{
    Audio,
    Bluetooth,
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

/// <summary>More kinds (displays, Fire TV, programs, credentials) arrive with their providers in M4.</summary>
public sealed record Library
{
    public static Library Empty { get; } = new();

    public ValueList<AudioDevice> Audio { get; init; } = [];

    public ValueList<BluetoothDevice> Bluetooth { get; init; } = [];

    public bool Contains(LibraryKind kind, string key) => kind switch
    {
        LibraryKind.Audio => Audio.Any(d => d.Key == key),
        LibraryKind.Bluetooth => Bluetooth.Any(d => d.Key == key),
        _ => false,
    };
}
