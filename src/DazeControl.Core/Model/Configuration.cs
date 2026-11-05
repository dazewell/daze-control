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

/// <summary>
/// One <c>settings.stepDefaults</c> entry (D-080): what steps of a type use when they set nothing themselves.
/// Every field is optional and resolves on its own (own &gt; type default &gt; built-in).
/// </summary>
public sealed record StepDefault
{
    public TimeSpan? Timeout { get; init; }

    public int? RetryCount { get; init; }

    public TimeSpan? RetryDelay { get; init; }

    public FailurePolicy? OnFailure { get; init; }

    /// <summary>"Only if" for every step of the type (D-081, B8): a condition id of the type; <c>always</c> is a valid entry.</summary>
    public string? Only { get; init; }

    public bool IsEmpty => this == new StepDefault();
}

public sealed record Settings
{
    public PopupSettings Popup { get; init; } = new();

    /// <summary>Keyed by step id ("audio.setDefault").</summary>
    public ValueDictionary<StepDefault> StepDefaults { get; init; }
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
