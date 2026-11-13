using DazeControl.Core.Layout;
using DazeControl.Core.Model;
using DazeControl.Editor.ViewModels.Canvas;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>Spread lanes (F-040) in the drag preview: what the drag shows is what the drop commits (D-120).</summary>
public sealed class DragPreviewLaneTests
{
    private static StepNode Step(string id, params string[] after) =>
        new(id, "wait") { After = [.. after.Select(a => new Edge(a))] };

    /// <summary>a → b → c → d, and j waits for all four: in Top to bottom a, b and c skip to j along lanes.</summary>
    private static readonly Flow WideJoin = new("f", "F", [Step("a"), Step("b", "a"), Step("c", "b"), Step("d", "c"), Step("j", "a", "b", "c", "d")]);

    [Fact]
    public void LinesThatShareALaneKeepTheirSpreadWhileAnotherStepIsDragged()
    {
        var options = LayoutOptions.Default with { Shape = LayoutShape.TopToBottom };
        var frozen = FlowPlacement.Arrange(WideJoin, _ => new Size(232, 56), options);
        Assert.Equal(3, frozen.Edges.Count(e => e.Lane is not null));

        var preview = new DragPreview(frozen, ["d"], options);
        var frame = preview.At(new Point(0, 0));

        // The still detours (a, b, c → j) are routed again and spread as the commit spread them: none is redrawn.
        Assert.DoesNotContain(frame.Routes, r => r.From is "a" or "b" && r.To == "j");
        // The one that touches d's neighbour keeps its lane too.
        Assert.All(frame.Routes.Where(r => r.Lane is not null), r => Assert.Contains(r, frozen.Edges));
    }
}
