using DazeControl.Core.Model;

namespace DazeControl.Core.Catalog;

/// <summary>What a step value holds (D-129). Duration later, if a step needs it.</summary>
public enum ValueKind
{
    /// <summary>As given, trimmed.</summary>
    Text,

    /// <summary>Invariant digits ("35"); units are the person's text.</summary>
    Number,

    /// <summary>A Library key; text shows the item's name, a device setting gets the key.</summary>
    Device,
}

/// <summary>
/// A value a step type gives to later steps (D-129, DESIGN §4.1): <c>{bt.device}</c> in a text setting that takes values.
/// <see cref="Name"/> is the YAML word (<c>[a-z][A-Za-z0-9]*</c>); <see cref="Title"/> is what the editor and dry runs show.
/// </summary>
public sealed record ValueSpec(string Name, ValueKind Kind, string Title)
{
    /// <summary>For <see cref="ValueKind.Device"/>: the Library kind; null = decided by the step (a Pick's <c>kind</c>).</summary>
    public LibraryKind? Library { get; init; }

    /// <summary>"The Library name of the device it connected".</summary>
    public string? Help { get; init; }

    /// <summary>Never in logs, history or summaries (DESIGN §4.8).</summary>
    public bool Sensitive { get; init; }

    /// <summary>Still given when the step fails (exit code, status).</summary>
    public bool OnFailure { get; init; }
}
