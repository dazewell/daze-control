using CtrlAltDaze.Core.Layout;
using CtrlAltDaze.Editor.ViewModels.Canvas;

namespace CtrlAltDaze.Editor.ViewModels.Tests;

/// <summary>The canvas camera (D-119 e): screen↔world, zoom keeps the point under the pointer, fit, 48-DIP clamp, reveal.</summary>
public sealed class CameraTests
{
    private const double Vw = 1000, Vh = 600;

    [Theory]
    [InlineData(0, 0, 1, 0, 0)]
    [InlineData(-340, 125, 0.5, 812, 47)]
    [InlineData(77.5, -1900, 2, -30, 2400)]
    public void ScreenAndWorldRoundTrip(double x, double y, double zoom, double px, double py)
    {
        var camera = new Camera(x, y, zoom);
        var p = new Point(px, py);
        AssertNear(p, camera.ToWorld(camera.ToScreen(p)));
        AssertNear(p, camera.ToScreen(camera.ToWorld(p)));
        var r = camera.ToScreen(new Rect(px, py, 100, 40));
        AssertNear(camera.ToScreen(p), new Point(r.X, r.Y));
        Assert.Equal(100 * zoom, r.Width, 6);
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(1.37)]
    [InlineData(2.0)]
    public void ZoomKeepsThePointUnderThePointer(double target)
    {
        var start = Camera.Fit(1, 800, 300, Vw, Vh).PanBy(-123, 45);
        var (sx, sy) = (420.0, 250.0);
        var world = start.ToWorld(new Point(sx, sy));

        var camera = start.ZoomAt(target, sx, sy);

        Assert.Equal(target, camera.Zoom, 6);
        AssertNear(new Point(sx, sy), camera.ToScreen(world));
    }

    [Fact]
    public void ZoomSurvivesRoundTrips()
    {
        var camera = Camera.Fit(1, 800, 300, Vw, Vh);
        var world = camera.ToWorld(new Point(130, 200));
        for (var i = 0; i < 5; i++)
            camera = camera.ZoomAt(camera.Zoom * 1.1, 130, 200);
        for (var i = 0; i < 5; i++)
            camera = camera.ZoomAt(camera.Zoom / 1.1, 130, 200);
        Assert.Equal(1, camera.Zoom, 6);
        AssertNear(new Point(130, 200), camera.ToScreen(world));
    }

    [Theory]
    [InlineData(0.01, 0.1)] // F-006: down to 10 %
    [InlineData(0.1, 0.1)]
    [InlineData(2.0, 2.0)]
    [InlineData(9.0, 2.0)]
    public void ZoomClampsAtBothEnds(double asked, double expected) =>
        Assert.Equal(expected, Camera.Identity.ZoomAt(asked, 500, 300).Zoom, 6);

    [Fact]
    public void NotchesMultiplyByATenthEach()
    {
        Assert.Equal(1.1, Camera.WheelFactor(120), 6);
        Assert.Equal(1 / 1.1, Camera.WheelFactor(-120), 6);
        Assert.Equal(1.21, Camera.WheelFactor(240), 6);
        Assert.Equal(Math.Pow(1.1, 0.25), Camera.WheelFactor(30), 6); // a pinch's fraction of a notch
    }

    [Theory]
    [InlineData(200, 100, 1.0)] // a flow smaller than the window
    [InlineData(3000, 1500, 1.0)] // a flow larger than the window
    [InlineData(200, 100, 2.0)]
    [InlineData(3000, 1500, 0.5)]
    public void ClampKeeps48DipOfTheStepsOnScreen(double w, double h, double zoom)
    {
        var inset = Camera.Inset; // the layout's margin around the steps
        // Panned far right/down: the steps' left/top edge stays 48 DIP inside the window's right/bottom edge.
        var far = new Camera(1e6, 1e6, zoom).Clamp(w, h, Vw, Vh);
        AssertNear(new Point(Vw - 48, Vh - 48), far.ToScreen(new Point(inset, inset)));
        // Panned far left/up: the steps' right/bottom edge stays 48 DIP inside the window's left/top edge.
        var near = new Camera(-1e6, -1e6, zoom).Clamp(w, h, Vw, Vh);
        AssertNear(new Point(48, 48), near.ToScreen(new Point(w - inset, h - inset)));
        // Anywhere in between is left alone: the pan is otherwise unbounded.
        var fit = Camera.Fit(zoom, w, h, Vw, Vh);
        Assert.Equal(fit, fit.Clamp(w, h, Vw, Vh));
    }

    [Fact]
    public void ATinyWindowStillGivesAFiniteCamera()
    {
        var camera = new Camera(500, -500, 1).Clamp(200, 100, 30, 30);
        Assert.True(double.IsFinite(camera.X) && double.IsFinite(camera.Y));
        Assert.Equal(new Camera(5, 6, 1), new Camera(5, 6, 1).Clamp(200, 100, 0, 0)); // not laid out yet: unchanged
    }

    [Fact]
    public void FitCentresAFlowThatFits()
    {
        var camera = Camera.Fit(1, 400, 200, Vw, Vh);
        AssertNear(new Point(Vw / 2, Vh / 2), camera.ToScreen(new Point(200, 100)));
    }

    [Fact]
    public void FitShowsTheStartOfAFlowTooBigAtTheSmallestZoom()
    {
        var camera = Camera.Fit(0.5, 4000, 200, Vw, Vh);
        Assert.Equal(0, camera.ToScreen(new Point(0, 0)).X, 6);
        Assert.Equal(Vh / 2, camera.ToScreen(new Point(0, 100)).Y, 6);
    }

    [Fact]
    public void RevealPansJustEnoughAndLeavesAVisibleRectAlone()
    {
        var camera = new Camera(0, 0, 1);
        var visible = new Rect(100, 100, 200, 60);
        Assert.Equal(camera, camera.Reveal(visible, Vw, Vh));

        var right = camera.Reveal(new Rect(1200, 100, 200, 60), Vw, Vh);
        Assert.Equal(Vw - Camera.RevealMargin, right.ToScreen(new Rect(1200, 100, 200, 60)).Right, 6);
        Assert.Equal(0, right.Y, 6);

        var above = new Camera(0, 0, 2).Reveal(new Rect(10, -300, 100, 40), Vw, Vh);
        Assert.Equal(Camera.RevealMargin, above.ToScreen(new Rect(10, -300, 100, 40)).Y, 6);

        var tooWide = camera.Reveal(new Rect(2000, 100, 5000, 60), Vw, Vh); // the start wins
        Assert.Equal(Camera.RevealMargin, tooWide.ToScreen(new Rect(2000, 100, 5000, 60)).X, 6);
    }

    [Theory]
    [InlineData(500, 300, 0, 0)] // the middle: still
    [InlineData(40, 300, 0, 0)] // the band's inner edge: still
    [InlineData(20, 300, -300, 0)] // half way into the left band: half speed, leftwards
    [InlineData(0, 300, -600, 0)] // at the edge: full speed (600 DIP/s, F-001)
    [InlineData(-150, 300, -600, 0)] // past it (captured): no faster
    [InlineData(990, 590, 450, 450)] // a corner pans along both axes
    [InlineData(500, 10, 0, -450)]
    public void EdgeSpeedGrowsWithDepthInTheBand(double sx, double sy, double vx, double vy)
    {
        var (x, y) = Camera.EdgeVelocity(sx, sy, Vw, Vh);
        Assert.Equal(vx, x, 6);
        Assert.Equal(vy, y, 6);
    }

    [Fact]
    public void AutoPanRunsOutWithTheContent()
    {
        // F-001: the steps span world 0..2000 × 0..200 at 100 %; the view shows 0..Vw.
        var content = new Rect(0, 0, 2000, 200);
        var camera = Camera.Identity;
        var right = (Camera.MaxEdgeSpeed, 0.0);
        Assert.Equal(right, camera.EdgeLimit(right, content, Vw, Vh)); // more steps that way: pans
        // Panned until one column's room past the last step is on screen: stops.
        var end = camera.PanBy(-(2000 + Camera.EdgeRoom - Vw), 0);
        Assert.Equal((0.0, 0.0), end.EdgeLimit(right, content, Vw, Vh));
        // Leftwards from the start: the room before the first step isn't on screen yet, so it still pans that far.
        var left = (-Camera.MaxEdgeSpeed, 0.0);
        Assert.Equal(left, camera.EdgeLimit(left, content, Vw, Vh));
        Assert.Equal((0.0, 0.0), camera.PanBy(Camera.EdgeRoom, 0).EdgeLimit(left, content, Vw, Vh));
        // Each axis on its own: the content is short, so downwards is already exhausted.
        Assert.Equal((Camera.MaxEdgeSpeed, 0.0), camera.EdgeLimit((Camera.MaxEdgeSpeed, Camera.MaxEdgeSpeed), content, Vw, Vh));
    }

    [Fact]
    public void AutoPanOfAFullSecondHeldAtTheEdgeIsCapped()
    {
        // F-001: 2 s held at the edge pans 1200 DIP at most (was > 1500), and frames never jump.
        var camera = Camera.Identity;
        var velocity = Camera.EdgeVelocity(Vw + 50, 300, Vw, Vh);
        for (var i = 0; i < 120; i++)
            camera = camera.EdgePan(velocity, 1 / 60.0);
        Assert.Equal(-2 * Camera.MaxEdgeSpeed, camera.X, 6);
    }

    [Fact]
    public void ATinyViewportNeverAutoPans() =>
        Assert.Equal((0.0, 0.0), Camera.EdgeVelocity(0, 0, 100, 100));

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(2.0)]
    public void EdgePanMovesTheWorldUnderTheStillPointerTheWayItPoints(double zoom)
    {
        var camera = new Camera(-200, 50, zoom);
        var pointer = new Point(Vw - 10, 300); // deep in the right band
        var before = camera.ToWorld(pointer);
        var velocity = Camera.EdgeVelocity(pointer.X, pointer.Y, Vw, Vh);

        var after = camera.EdgePan(velocity, 1 / 60.0);

        Assert.Equal(velocity.X / 60, camera.ToScreen(after.ToWorld(pointer)).X - pointer.X, 6); // screen DIP per frame
        Assert.Equal(velocity.X / 60 / zoom, after.ToWorld(pointer).X - before.X, 6); // the item under it moves right
        Assert.Equal(before.Y, after.ToWorld(pointer).Y, 6);
        Assert.Equal(zoom, after.Zoom);
    }

    [Fact]
    public void AStalledFrameIsCappedAndATimeTravelFrameIsStill()
    {
        var camera = Camera.Identity;
        var velocity = (Camera.MaxEdgeSpeed, 0.0);
        Assert.Equal(-Camera.MaxEdgeSpeed * Camera.MaxEdgeStep, camera.EdgePan(velocity, 2).X, 6);
        Assert.Equal(camera, camera.EdgePan(velocity, -1));
    }

    private static void AssertNear(Point expected, Point actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
    }
}
