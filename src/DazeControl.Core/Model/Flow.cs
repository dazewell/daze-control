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

    public WhenRunning WhenRunning { get; init; } = WhenRunning.Ignore;

    public bool Popup { get; init; } = true;

    public bool Enabled { get; init; } = true;

    public Node? FindNode(string id) => Nodes.FirstOrDefault(n => n.Id == id);
}
