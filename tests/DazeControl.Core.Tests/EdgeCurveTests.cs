using DazeControl.Core.Layout;

namespace DazeControl.Core.Tests;

/// <summary>Edge hit-testing is the distance to the drawn Bézier (UX §5.8 #2): 12 DIP mouse, 20 touch.</summary>
public class EdgeCurveTests
{
    // A typical edge between two columns that sit one row apart: an S-curve from (0,0) down to (200,100).
    private static readonly EdgeCurve Curve = EdgeCurve.Between(new Point(0, 0), new Point(206, 100));

    [Fact]
    public void PointsOnTheCurveAreAtZeroDistance()
    {
        foreach (var t in new[] { 0, 0.1, 0.25, 0.5, 0.75, 0.9, 1 })
            Assert.True(Curve.DistanceTo(Curve.At(t)) < 0.5, $"t={t}");
        Assert.True(Curve.DistanceTo(Curve.Tip) < 0.01);
    }

    [Fact]
    public void TheMidpointIsTheCentreOfTheSCurve()
    {
        Assert.Equal(100, Curve.Midpoint.X, 3);
        Assert.Equal(50, Curve.Midpoint.Y, 3);
    }

    [Theory]
    [InlineData(11, true, true)]
    [InlineData(13, false, true)]
    [InlineData(19, false, true)]
    [InlineData(21, false, false)]
    public void TheBandIsMeasuredAcrossTheCurveNotToItsEndpoints(double offset, bool mouse, bool touch)
    {
        // Step off the curve along its normal at the midpoint, where it is steepest (furthest from the chord).
        var a = Curve.At(0.49);
        var b = Curve.At(0.51);
        double dx = b.X - a.X, dy = b.Y - a.Y, length = Math.Sqrt(dx * dx + dy * dy);
        var p = new Point(Curve.Midpoint.X - dy / length * offset, Curve.Midpoint.Y + dx / length * offset);

        var distance = Curve.DistanceTo(p);

        Assert.Equal(offset, distance, 0.5);
        Assert.Equal(mouse, distance <= 12);
        Assert.Equal(touch, distance <= 20);
    }

    [Fact]
    public void FarFromTheCurveIsNoHit()
    {
        Assert.True(Curve.DistanceTo(new Point(0, 40)) > 20);
        Assert.True(Curve.DistanceTo(new Point(200, 0)) > 20);
    }

    [Fact]
    public void TheArrowheadCountsAsTheEdge()
    {
        Assert.True(Curve.DistanceTo(new Point(204, 104)) <= 12);
        Assert.True(Curve.DistanceTo(new Point(230, 100)) > 12);
    }
}

/// <summary>A wrapped edge (D-060): right 16, down to the gap, left to a lane 16 before the next row, down, right in.</summary>
public class WrappedEdgeCurveTests
{
    // Row end's port at (600, 76), gap middle at y 128, next row's first port at (48, 180).
    private static readonly EdgeCurve Curve = EdgeCurve.Wrapped(new Point(600, 76), new Point(48, 180), 128);

    [Fact]
    public void TheCornersFollowTheWrappedShape()
    {
        Assert.Equal(
            [new Point(600, 76), new Point(616, 76), new Point(616, 128), new Point(32, 128), new Point(32, 180), new Point(42, 180)],
            Curve.Corners);
        Assert.True(Curve.IsWrapped);
    }

    [Theory]
    [InlineData(608, 76, true)]   // the step out of the port
    [InlineData(626, 100, true)]  // beside the first drop, 10 away
    [InlineData(300, 139, true)]  // just below the return lane
    [InlineData(300, 145, false)] // 17 below it
    [InlineData(20, 160, true)]   // left of the lane's drop
    [InlineData(300, 76, false)]  // where a Bézier would have run, inside the row
    [InlineData(46, 180, true)]   // the arrowhead
    public void HitTestIsTheDistanceToThePolyline(double x, double y, bool within12)
    {
        Assert.Equal(within12, Curve.DistanceTo(new Point(x, y)) <= 12);
    }

    [Fact]
    public void TheMidpointLiesOnTheReturnLane()
    {
        Assert.Equal(new Point(324, 128), Curve.Midpoint);
        // Even when a tall card makes the drops longer than the lane.
        var tall = EdgeCurve.Wrapped(new Point(300, 100), new Point(48, 600), 400);
        Assert.Equal(new Point(174, 400), tall.Midpoint);
    }

    [Fact]
    public void CornersAreRoundedByAtMostEight()
    {
        var segments = Curve.RoundedSegments().ToList();
        Assert.Equal(5, segments.Count);
        Assert.Equal(new Point(608, 76), segments[0].LineTo);
        Assert.Equal(new Point(616, 84), segments[0].CurveTo);
        // The last corner's outgoing run is 10 long, so its radius is 5.
        Assert.Equal(new Point(37, 180), segments[3].CurveTo);
        Assert.Null(segments[^1].Corner);
    }
}
