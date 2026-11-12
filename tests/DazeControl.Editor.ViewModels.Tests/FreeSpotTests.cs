using DazeControl.Core.Layout;
using DazeControl.Editor.ViewModels.Canvas;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>A library drop lands where it was aimed, or at the nearest spot that covers no step (D-120).</summary>
public sealed class FreeSpotTests
{
    private const double Gap = 16;

    private static readonly Rect[] Taken = [new(48, 48, 232, 56), new(344, 48, 232, 56), new(48, 160, 300, 56)];

    private static bool Covers(Rect r, double reach) =>
        Taken.Any(b => r.X < b.Right + Gap && b.X < r.X + Math.Max(r.Width, reach) + Gap && r.Y < b.Bottom + Gap && b.Y < r.Bottom + Gap);

    [Fact]
    public void AClearSpotIsKept()
    {
        var want = new Rect(48, 400, 232, 56);
        Assert.Equal(want, FreeSpot.Nearest(want, 320, Taken, Gap));
    }

    [Theory]
    [InlineData(64, 56)] // right on the first step
    [InlineData(200, 80)] // between the first two
    [InlineData(120, 176)] // on the wide one below
    [InlineData(-40, 40)] // half off the left
    public void AnOverlappingSpotMovesToTheNearestClearOne(double x, double y)
    {
        var want = new Rect(x, y, 232, 56);

        var spot = FreeSpot.Nearest(want, 320, Taken, Gap);

        Assert.False(Covers(spot, 320));
        Assert.Equal(want.Width, spot.Width);
        Assert.Equal(0, spot.X % FlowPlacement.Grid);
        Assert.Equal(0, spot.Y % FlowPlacement.Grid);
        // Nearest: no clear grid spot within the distance it moved (checked on a coarse grid around the wanted rect).
        var moved = Math.Sqrt((spot.X - x) * (spot.X - x) + (spot.Y - y) * (spot.Y - y));
        for (var dx = -600.0; dx <= 600; dx += 8)
        {
            for (var dy = -400.0; dy <= 400; dy += 8)
            {
                if (Math.Sqrt(dx * dx + dy * dy) < moved - 1e-6)
                    Assert.True(Covers(want with { X = x + dx, Y = y + dy }, 320), $"({x + dx}, {y + dy}) was clear and nearer");
            }
        }
    }

    [Fact]
    public void RoomToWidenCountsSoALongTitleCantGrowIntoTheNextStep()
    {
        var want = new Rect(48, 400, 232, 56);
        var taken = new[] { new Rect(296, 400, 232, 56) }; // 16 right of a 232-wide pill, inside a 320-wide one
        var spot = FreeSpot.Nearest(want, 320, taken, Gap);
        Assert.NotEqual(want, spot);
        Assert.False(new Rect(spot.X, spot.Y, 320, 56).Intersects(taken[0]));
    }

    [Fact]
    public void NothingTakenKeepsTheWantedSpot() =>
        Assert.Equal(new Rect(8, 8, 232, 56), FreeSpot.Nearest(new Rect(8, 8, 232, 56), 320, [], Gap));
}
