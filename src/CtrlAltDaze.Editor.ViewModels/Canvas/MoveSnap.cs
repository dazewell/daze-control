using CtrlAltDaze.Core.Layout;

namespace CtrlAltDaze.Editor.ViewModels.Canvas;

/// <summary>
/// The offset a free move really applies (D-120, UX §5.9 "after any drop: columns aligned, gaps 64/16, no overlap"):
/// the lead (the box under the pointer) snaps to the 8-DIP grid, or to a step's column when its left edge comes within
/// half a column gap of it; then, when the moved boxes would come closer to a step that stays put than the column gap
/// beside it or the row gap above or below it, the move takes the nearest offset that keeps both gaps. Nothing stops it
/// above or left of the flow: the layout then moves the flow's origin, as a library drop does (F-030). The drag preview
/// and <see cref="FlowCanvasViewModel.MoveBy"/> both use it, so a drop lands exactly where it was shown.
/// </summary>
public static class MoveSnap
{
    /// <param name="movers">The moved top-level boxes where they are now, the lead first.</param>
    /// <param name="others">The top-level boxes that stay put.</param>
    /// <param name="dx">The pointer's horizontal travel since the press.</param>
    /// <param name="dy">The pointer's vertical travel since the press.</param>
    /// <param name="columnGap">The least room beside a step (the layout's column gap, 64 at Normal spacing).</param>
    /// <param name="rowGap">The least room above or below a step (the layout's row gap, 16 at Normal spacing).</param>
    public static Point Offset(IReadOnlyList<Rect> movers, IReadOnlyList<Rect> others, double dx, double dy, double columnGap, double rowGap)
    {
        ArgumentNullException.ThrowIfNull(movers);
        ArgumentNullException.ThrowIfNull(others);
        if (movers.Count == 0)
            return default;
        var lead = movers[0];
        dx = FlowPlacement.Snap(lead.X + dx) - lead.X;
        dy = FlowPlacement.Snap(lead.Y + dy) - lead.Y;
        // Columns line up: the nearest step's left edge within half a column gap wins over the grid.
        var column = double.NaN;
        foreach (var b in others)
        {
            var d = Math.Abs(b.X - (lead.X + dx));
            if (d <= columnGap / 2 && (double.IsNaN(column) || d < Math.Abs(column - (lead.X + dx))))
                column = b.X;
        }
        if (!double.IsNaN(column))
            dx = column - lead.X;
        return Clear(movers, others, dx, dy, columnGap, rowGap) ? new Point(dx, dy) : Nearest(movers, others, dx, dy, columnGap, rowGap);
    }

    /// <summary>
    /// The nearest clear offset: it puts some moved box against a still box's side (a gap away), in its column or row,
    /// or keeps the wanted coordinate, along each axis. Right of every still box is always clear, so one is found.
    /// </summary>
    private static Point Nearest(IReadOnlyList<Rect> movers, IReadOnlyList<Rect> others, double dx, double dy, double columnGap, double rowGap)
    {
        var xs = new HashSet<double> { dx };
        var ys = new HashSet<double> { dy };
        foreach (var m in movers)
        {
            foreach (var b in others)
            {
                xs.Add(b.Right + columnGap - m.X);
                xs.Add(b.X - columnGap - m.Right);
                xs.Add(b.X - m.X);
                ys.Add(b.Bottom + rowGap - m.Y);
                ys.Add(b.Y - rowGap - m.Bottom);
                ys.Add(b.Y - m.Y);
            }
        }
        var byX = xs.OrderBy(x => Math.Abs(x - dx)).ToList();
        var byY = ys.OrderBy(y => Math.Abs(y - dy)).ToList();
        var best = (X: dx, Y: dy, D: double.MaxValue);
        foreach (var x in byX)
        {
            var ex = (x - dx) * (x - dx);
            if (ex >= best.D)
                break; // sorted: every later x is farther
            foreach (var y in byY)
            {
                var d = ex + (y - dy) * (y - dy);
                if (d >= best.D)
                    break;
                if (Clear(movers, others, x, y, columnGap, rowGap))
                {
                    best = (x, y, d);
                    break; // the nearest y for this x
                }
            }
        }
        return new Point(best.X, best.Y);
    }

    /// <summary>
    /// Whether no moved box, offset by (dx, dy), comes nearer a still box than <paramref name="columnGap"/> beside it
    /// or <paramref name="rowGap"/> above or below it (a box closer both ways overlaps or crowds it).
    /// </summary>
    public static bool Clear(IReadOnlyList<Rect> movers, IReadOnlyList<Rect> others, double dx, double dy, double columnGap, double rowGap)
    {
        ArgumentNullException.ThrowIfNull(movers);
        ArgumentNullException.ThrowIfNull(others);
        const double Eps = 1e-6;
        foreach (var m in movers)
        {
            double x = m.X + dx, y = m.Y + dy;
            foreach (var b in others)
            {
                if (x < b.Right + columnGap - Eps && b.X < x + m.Width + columnGap - Eps
                    && y < b.Bottom + rowGap - Eps && b.Y < y + m.Height + rowGap - Eps)
                    return false;
            }
        }
        return true;
    }
}
