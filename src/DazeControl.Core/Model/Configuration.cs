namespace DazeControl.Core.Model;

/// <summary>The OSD's six positions.</summary>
public enum PopupPosition
{
    BottomCenter,
    TopCenter,
    TopLeft,
    TopRight,
    BottomLeft,
    BottomRight,
}

public sealed record PopupSettings(PopupPosition Position = PopupPosition.BottomCenter, int Seconds = 3);

public sealed record Settings
{
    public PopupSettings Popup { get; init; } = new();
}

/// <summary>The whole of config.yaml after mapping (schema <see cref="CurrentVersion"/>).</summary>
public sealed record Configuration
{
    public const int CurrentVersion = 1;

    public static Configuration Empty { get; } = new();

    public Settings Settings { get; init; } = new();

    public Library Library { get; init; } = Library.Empty;

    /// <summary>YAML order = tray order.</summary>
    public ValueList<Flow> Flows { get; init; } = [];
}
