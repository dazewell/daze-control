using DazeControl.Core.Layout;

namespace DazeControl.Editor.ViewModels.Canvas;

/// <summary>
/// One row of the Outline view (UX §5.2), the canvas's keyboard and screen-reader equivalent: steps in run order,
/// a card as a branch holding its steps.
/// </summary>
public sealed record OutlineItem(string Id, string Title, string Summary, string Glyph, string AutomationName, IReadOnlyList<OutlineItem> Children)
{
    public override string ToString() => AutomationName;
}

/// <summary>Steps copied with Ctrl+C; one per editor window, so a copy survives switching flows.</summary>
public sealed class CanvasClipboard
{
    public FlowClip? Clip { get; set; }
}
