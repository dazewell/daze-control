using DazeControl.Core.Layout;

namespace DazeControl.Editor.ViewModels.Canvas;

/// <summary>
/// The offset a free move really applies (D-120): the lead (the box under the pointer) snaps to the 8-DIP grid, the
/// rest keep their distance from it, and nothing goes above or left of the padding. The drag preview and
/// <see cref="FlowCanvasViewModel.MoveBy"/> both use it, so a drop lands exactly where it was shown.
/// </summary>
public static class MoveSnap
{
    /// <param name="lead">The lead box's top left.</param>
    /// <param name="topLeft">The smallest X and the smallest Y over every moving box.</param>
    /// <param name="dx">The pointer's horizontal travel since the press.</param>
    /// <param name="dy">The pointer's vertical travel since the press.</param>
    /// <param name="padding">The layout's margin; no box goes above or left of it.</param>
    public static Point Offset(Point lead, Point topLeft, double dx, double dy, double padding)
    {
        dx = FlowPlacement.Snap(lead.X + dx) - lead.X;
        dy = FlowPlacement.Snap(lead.Y + dy) - lead.Y;
        return new Point(Math.Max(dx, padding - topLeft.X), Math.Max(dy, padding - topLeft.Y));
    }
}
