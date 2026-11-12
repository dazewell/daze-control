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

/// <summary>Where a param's suggested values come from (D-050).</summary>
public enum SuggestSource
{
    /// <summary>The engine's live list: <c>devices &lt;kind&gt;</c> (displays, services, credentials, adb).</summary>
    Devices,

    /// <summary>The open config: flag names, programs, the inputs of the Android device in param <c>device</c>.</summary>
    Config,

    /// <summary>A fixed table in Core that the provider also uses (Android keys).</summary>
    Static,
}

/// <summary>A param's suggestions. Suggestions never restrict a value; validation is unchanged (D-050).</summary>
public sealed record ParamSuggest(SuggestSource Source, string Kind)
{
    /// <summary>For <see cref="SuggestSource.Static"/>.</summary>
    public ValueList<string> Values { get; init; } = [];

    /// <summary>Config kinds (<see cref="SuggestSource.Config"/>).</summary>
    public const string Flags = "flags", AndroidInputs = "androidInputs", Flows = "flows";

    /// <summary>A <see cref="SuggestSource.Devices"/> kind that needs the step's Android device: the apps installed on it (D-092).</summary>
    public const string AndroidApps = "androidApps";

    public static ParamSuggest Devices(string kind) => new(SuggestSource.Devices, kind);

    public static ParamSuggest Config(string kind) => new(SuggestSource.Config, kind);

    public static ParamSuggest Static(string kind, ValueList<string> values) => new(SuggestSource.Static, kind) { Values = values };
}

/// <summary>What else in the config a param's value names, so a rename can rewrite it and validation can follow it.</summary>
public enum ConfigRef
{
    /// <summary>A flow's YAML key (<c>flow.run</c>; M12's <c>menu.show</c> items reuse it).</summary>
    Flow,
}

/// <summary>A value the inspector can fill in: "Lock the PC" → <c>rundll32.exe user32.dll,LockWorkStation</c>.</summary>
public sealed record ParamExample(string Title, string Value);

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

    /// <summary>
    /// Plain words for <see cref="Choices"/>, same order (D-102). Display only: YAML keeps the value. Empty = show the value.
    /// </summary>
    public ValueList<string> Captions { get; init; } = [];

    /// <summary>The words for a choice; the value itself when it has no caption.</summary>
    public string CaptionOf(string value)
    {
        var i = Choices.ToList().IndexOf(value);
        return i >= 0 && i < Captions.Count && Captions[i].Length > 0 ? Captions[i] : value;
    }

    /// <summary>For <see cref="ParamKind.LibraryRef"/>.</summary>
    public LibraryKind? Library { get; init; }

    /// <summary>
    /// A <see cref="ParamKind.Text"/> that holds a Library key of the kind another setting of the step chooses (<c>prompt.pick</c>'s
    /// <c>default</c>, D-128): the inspector shows a picker of that kind's items. Text so a hand-typed name still loads.
    /// </summary>
    public string? LibraryFrom { get; init; }

    /// <summary>Where suggested values come from; the inspector offers them (D-050).</summary>
    public ParamSuggest? Suggest { get; init; }

    /// <summary>A <see cref="ParamKind.TextList"/> whose order matters (keys sent in order): a token list, not a checklist.</summary>
    public bool Ordered { get; init; }

    /// <summary>Values that show how to fill a free-text param; the inspector's "Examples" menu.</summary>
    public ValueList<ParamExample> Examples { get; init; } = [];

    /// <summary>The value is the key of another part of the config (a flow).</summary>
    public ConfigRef? RefersTo { get; init; }

    public int? Min { get; init; }

    public int? Max { get; init; }
}
