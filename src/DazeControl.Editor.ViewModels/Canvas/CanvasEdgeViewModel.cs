using CommunityToolkit.Mvvm.ComponentModel;
using DazeControl.Core.Layout;
using DazeControl.Core.Model;

namespace DazeControl.Editor.ViewModels.Canvas;

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
    internal CanvasEdgeViewModel(EdgeRoute route, bool intoMayFail)
    {
        From = route.From;
        To = route.To;
        When = route.When;
        Curve = route.WrapLaneY is { } lane ? EdgeCurve.Wrapped(route.Start, route.End, lane) : EdgeCurve.Between(route.Start, route.End);
        Style = route.When switch
        {
            EdgeWhen.Failure => EdgeStyle.IfFails,
            EdgeWhen.Always => EdgeStyle.Always,
            _ => intoMayFail ? EdgeStyle.IntoMayFail : EdgeStyle.Normal,
        };
    }

    public string From { get; }

    public string To { get; }

    public EdgeWhen When { get; }

    public EdgeCurve Curve { get; }

    public EdgeStyle Style { get; }

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

/// <summary>Where a dragged pill lands over another (UX §5.2): body = "Run together", right third = "Run after".</summary>
public enum DropZone
{
    None,
    RunTogether,
    RunAfter,
}
