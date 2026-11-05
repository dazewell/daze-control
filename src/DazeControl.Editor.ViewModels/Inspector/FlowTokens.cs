using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Core.Validation;

namespace DazeControl.Editor.ViewModels.Inspector;

/// <summary>One flow of the menu's list (UX §5.4, DESIGN §3.7): glyph + name, and #38 / #40 when it can't be offered.</summary>
public sealed record FlowTokenViewModel(string Id, string Name, string Glyph, string Caption, string? Problem, int Index, int Count)
{
    /// <summary>The list it belongs to, for the row's remove and move keys.</summary>
    public ParamFieldViewModel? Owner { get; init; }

    public bool IsMissing => Caption == FlowTokens.Gone;

    public bool HasProblem => Problem is not null;

    public string RemoveName => $"Remove {Name}";

    /// <summary>"Couch TV, Turned off, 2 of 4. A menu can't list its own flow."</summary>
    public string AutomationName => $"{Name}, {(Caption.Length > 0 ? Caption + ", " : "")}{Index + 1} of {Count}{(Problem is null ? "" : ". " + Problem)}";
}

/// <summary>A flow the "Add a flow" box offers.</summary>
public sealed record FlowChoice(string Id, string Name, string Glyph, string Caption)
{
    public override string ToString() => Name;
}

/// <summary>Builds the menu's token rows and the add box's choices from the open config.</summary>
public static class FlowTokens
{
    public const string Gone = "Isn't there anymore";

    /// <summary>UX #38.</summary>
    public const string OwnFlow = "A menu can't list its own flow.";

    public static string MissingText(string id) => $"{id} isn't there anymore. Remove it from the menu.";

    /// <summary>Menu order; a deleted flow stays as a caution row so nothing changes by itself (#40).</summary>
    public static List<FlowTokenViewModel> Rows(Configuration config, IReadOnlyList<Problem> problems, string selfId, IReadOnlyList<string> items)
    {
        var rows = new List<FlowTokenViewModel>();
        for (var i = 0; i < items.Count; i++)
        {
            var id = items[i].Trim();
            var flow = config.Flows.FirstOrDefault(f => f.Id == id);
            rows.Add(flow is null
                ? new(id, id, FlowGlyph.Decode("E8FD"), Gone, MissingText(id), i, items.Count)
                : new(id, flow.Name, FlowGlyph.Decode(FlowGlyph.CodeOf(flow)), Caption(flow, problems), id == selfId ? OwnFlow : null, i, items.Count));
        }
        return rows;
    }

    /// <summary>Every other flow not listed yet, by name.</summary>
    public static List<FlowChoice> Choices(Configuration config, IReadOnlyList<Problem> problems, string selfId, IReadOnlyList<string> items) =>
    [
        .. config.Flows
            .Where(f => f.Id != selfId && !items.Any(i => i.Trim() == f.Id))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(f => new FlowChoice(f.Id, f.Name, FlowGlyph.Decode(FlowGlyph.CodeOf(f)), Caption(f, problems))),
    ];

    private static string Caption(Flow flow, IReadOnlyList<Problem> problems) =>
        !flow.Enabled ? "Turned off"
        : problems.Any(p => p.Severity == Severity.Error && p.FlowId == flow.Id) ? "Needs fixing"
        : "";
}

/// <summary>Words for the editor's side of the controller trigger (DESIGN §3.7); the engine writes the same texts into history.</summary>
public static class TriggerTexts
{
    /// <summary>"Controller · View + Menu".</summary>
    public static string Controller(string chord) =>
        "Controller · " + (Core.Input.GamepadChord.TryParse(chord, out var parsed) ? parsed.Display : chord);

    /// <summary>"Menu · Couch": a flow launched from a menu.</summary>
    public static string Menu(string menuFlowName) => "Menu · " + menuFlowName;
}
