using CtrlAltDaze.Core.Validation;

namespace CtrlAltDaze.Editor.ViewModels.Canvas;

/// <summary>
/// One row of the flow editor's Problems list (UX §5.1 "Problems (n)"). <see cref="NodeId"/> is the step or card it
/// is about, null for the flow's own settings; selecting the row selects that node.
/// </summary>
public sealed record ProblemItem(Severity Severity, string Message, string Where, string? NodeId)
{
    public bool IsError => Severity == Severity.Error;

    /// <summary>Segoe Fluent Icons: error badge, caution (UX §5.3 "problem E7BA").</summary>
    public string Glyph => IsError ? "" : "";

    public string AutomationName => $"{(IsError ? "Problem" : "Warning")}: {Where}. {Message}";

    public override string ToString() => AutomationName;
}
