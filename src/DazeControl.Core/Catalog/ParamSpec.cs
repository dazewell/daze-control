using DazeControl.Core.Model;

namespace DazeControl.Core.Catalog;

/// <summary>What a param holds; picks the inspector control (UX 5.4) and the validation rule.</summary>
public enum ParamKind
{
    Text,

    /// <summary>Several lines (scripts); monospace in the inspector.</summary>
    MultilineText,

    /// <summary>Whole number within <see cref="ParamSpec.Min"/>..<see cref="ParamSpec.Max"/>.</summary>
    WholeNumber,
    Boolean,

    /// <summary>"20s", "500ms" (<see cref="Duration"/>).</summary>
    Duration,

    /// <summary>One of <see cref="ParamSpec.Choices"/>.</summary>
    Choice,

    /// <summary>Key of a <see cref="ParamSpec.Library"/> item.</summary>
    LibraryRef,

    /// <summary>Path to a file; existence is checked at run time, not on load (the drive may come later).</summary>
    FilePath,

    /// <summary>Path to a folder (a program's working directory); checked at run time like <see cref="FilePath"/>.</summary>
    FolderPath,

    /// <summary>YAML list of scalars, e.g. command-line args.</summary>
    TextList,

    /// <summary>YAML mapping of scalars, e.g. HTTP headers.</summary>
    TextMap,
}

/// <summary>
/// One param of a provider: the inspector renders it, validation checks <c>with:</c> against it (ARCHITECTURE →
/// Adding a provider). <see cref="Name"/> is the YAML key.
/// </summary>
public sealed record ParamSpec(string Name, ParamKind Kind, string Title)
{
    public bool Required { get; init; }

    /// <summary>Used when the param is left out; shown as the placeholder.</summary>
    public string? Default { get; init; }

    public string? Help { get; init; }

    /// <summary>For <see cref="ParamKind.Choice"/>, written as in YAML.</summary>
    public ValueList<string> Choices { get; init; } = [];

    /// <summary>For <see cref="ParamKind.LibraryRef"/>.</summary>
    public LibraryKind? Library { get; init; }

    public int? Min { get; init; }

    public int? Max { get; init; }
}
