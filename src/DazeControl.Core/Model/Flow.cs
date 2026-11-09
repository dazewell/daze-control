namespace DazeControl.Core.Model;

/// <summary>What a trigger does while the flow is still running (FEATURES F1).</summary>
public enum WhenRunning
{
    Ignore,
    Queue,
    Restart,
}

public sealed record TraySettings
{
    public bool Show { get; init; } = true;

    /// <summary>Submenu name; null = top level.</summary>
    public string? Folder { get; init; }

    /// <summary>Line above the item.</summary>
    public bool Separator { get; init; }
}

/// <summary>
/// A flow input (D-064): a Library device of <paramref name="Kind"/> that a <c>flow.run</c> caller may pass, else
/// <paramref name="Default"/>. <paramref name="Label"/> is what the editor and messages show ("Output").
/// </summary>
/// <param name="Ask">D-107: the person is asked at the start of a run (a prompt offering the Library items of <paramref name="Kind"/>); the default is the answer when none comes.</param>
/// <param name="Timeout">How long the prompt waits; null = 30 s.</param>
/// <param name="OnTimeout"><c>stop</c> or <c>use-default</c> (<see cref="Prompts"/>); empty = use-default when a person started the run, stop otherwise.</param>
public sealed record FlowInput(LibraryKind Kind, string Label, string Default, bool Ask = false, TimeSpan? Timeout = null, string OnTimeout = "");

/// <summary>
/// A named DAG of nodes (D-003). <see cref="Nodes"/> order is the order within a canvas column (D-020) and
/// carries no run-order meaning: edges do.
/// </summary>
public sealed record Flow(string Id, string Name, ValueList<Node> Nodes)
{
    /// <summary>Segoe Fluent Icons code point as hex text, e.g. "E702"; null = first step's glyph.</summary>
    public string? Glyph { get; init; }

    public TraySettings Tray { get; init; } = new();

    /// <summary>As written ("Alt+Z"); validation parses them with <see cref="Input.Hotkey"/>.</summary>
    public ValueList<string> Hotkeys { get; init; } = [];

    /// <summary>Ways the flow starts by itself (M20): schedules and system events. Bad ones are dropped on load with a warning.</summary>
    public ValueList<Trigger> Triggers { get; init; } = [];

    public WhenRunning WhenRunning { get; init; } = WhenRunning.Ignore;

    public bool Popup { get; init; } = true;

    public bool Enabled { get; init; } = true;

    /// <summary>Devices a caller may pass (D-064), by input name (<c>[a-z][a-z0-9-]*</c>); steps use them as <c>$name</c>.</summary>
    public ValueDictionary<FlowInput> Inputs { get; init; }

    public Node? FindNode(string id) => Nodes.FirstOrDefault(n => n.Id == id);
}
