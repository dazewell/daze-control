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
