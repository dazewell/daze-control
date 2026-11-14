using CommunityToolkit.Mvvm.ComponentModel;
using CtrlAltDaze.Core.Layout;
using CtrlAltDaze.Core.Model;

namespace CtrlAltDaze.Editor.ViewModels.Canvas;

/// <summary>How an edge is drawn (UX §5.3); the control batches edges of one style into one path.</summary>
public enum EdgeStyle
{
    Normal,

    /// <summary>Into a may-fail step: dashed 4/4.</summary>
    IntoMayFail,

    /// <summary><c>when: failure</c>: critical, dashed, "If it fails" chip.</summary>
    IfFails,

    /// <summary><c>when: always</c>: "Whatever happens" chip.</summary>
    Always,
}

public sealed partial class CanvasEdgeViewModel : ObservableObject
{
    /// <summary>One line of the scene; the same instance follows it while it exists (selection and hover stay on it).</summary>
    internal CanvasEdgeViewModel(SceneEdge edge)
    {
        From = edge.From;
        To = edge.To;
        Restyle(edge);
    }

    /// <summary>The line was re-routed or restyled (T21.6): the instance stays, so hover and selection stay on it.</summary>
    internal void Restyle(SceneEdge edge)
    {
        When = edge.Route.When;
        Curve = edge.Curve;
        Style = edge.Style;
    }

    public string From { get; }

    public string To { get; }

    public EdgeWhen When { get; private set; }

    public EdgeCurve Curve { get; private set; }

    public EdgeStyle Style { get; private set; }

    /// <summary>Text on the midpoint chip; null = no chip.</summary>
    public string? Chip => Style switch
    {
        EdgeStyle.IfFails => "If it fails",
        EdgeStyle.Always => "Whatever happens",
        _ => null,
    };

    [ObservableProperty]
    public partial bool IsSelected { get; internal set; }

    /// <summary>Under the pointer (set through <see cref="FlowCanvasViewModel.HoveredEdge"/>).</summary>
    [ObservableProperty]
    public partial bool IsHovered { get; internal set; }

    /// <summary>During a run: accent once its source finished (UX §5.3).</summary>
    [ObservableProperty]
    public partial bool IsLit { get; internal set; }
}

/// <summary>Where a dragged pill lands over another (UX §5.2): body = "Run together", left third = "Run before", right third = "Run after".</summary>
public enum DropZone
{
    None,
    RunTogether,
    RunAfter,
    RunBefore,
}
