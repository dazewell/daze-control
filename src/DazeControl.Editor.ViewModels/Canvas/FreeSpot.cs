using DazeControl.Core.Layout;

namespace DazeControl.Editor.ViewModels.Canvas;

/// <summary>
/// Where a new box goes so it covers nothing (D-120: a library drop never lands on top of another step): the wanted
/// rect if it's clear, else the nearest grid position that keeps <c>gap</c> DIP from every taken box.
/// </summary>
public static class FreeSpot
{
    /// <param name="want">The wanted rect (top left on the grid).</param>
    /// <param name="reach">How far right the box may still grow (a pill widens to its title after it lands); counted in
    /// the clearance, not in the returned rect.</param>
    /// <param name="taken">The boxes already on the canvas.</param>
    /// <param name="gap">The least room kept between boxes.</param>
    public static Rect Nearest(Rect want, double reach, IReadOnlyCollection<Rect> taken, double gap)
    {
        ArgumentNullException.ThrowIfNull(taken);
        var width = Math.Max(want.Width, reach);
        if (Clear(want.X, want.Y))
            return want;
        // The nearest clear spot sits against some box's side along each axis (or keeps the wanted coordinate): try those
        // combinations, nearest first. Right of every box is always clear, so one is found.
        var xs = new List<double> { want.X };
        var ys = new List<double> { want.Y };
        foreach (var b in taken)
        {
            xs.Add(Up(b.Right + gap));
            xs.Add(Down(b.X - gap - width));
            ys.Add(Up(b.Bottom + gap));
            ys.Add(Down(b.Y - gap - want.Height));
        }
        var best = (X: want.X, Y: want.Y, D: double.MaxValue);
        foreach (var x in xs.Distinct())
        {
            foreach (var y in ys.Distinct())
            {
                var d = (x - want.X) * (x - want.X) + (y - want.Y) * (y - want.Y);
                if (d < best.D && Clear(x, y))
                    best = (x, y, d);
            }
        }
        return want with { X = best.X, Y = best.Y };

        bool Clear(double x, double y) => !taken.Any(b =>
            x < b.Right + gap && b.X < x + width + gap && y < b.Bottom + gap && b.Y < y + want.Height + gap);
    }

    private static double Up(double v) => Math.Ceiling(v / FlowPlacement.Grid) * FlowPlacement.Grid;

    private static double Down(double v) => Math.Floor(v / FlowPlacement.Grid) * FlowPlacement.Grid;
}
