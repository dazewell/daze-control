using DazeControl.Editor.ViewModels.Canvas;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>Wheel zoom keeps the point under the pointer; pan room keeps 48 DIP of the flow on screen (D-074).</summary>
public sealed class CanvasViewportTests
{
    private const double Vw = 1000, Vh = 600;

    // Where a flow point lands on screen for a given view (the inverse of what ZoomAt solves).
    private static double ScreenX(CanvasView view, double hostX, double viewport) =>
        (hostX + CanvasViewport.Pad(viewport, view.Zoom)) * view.Zoom - view.OffsetX;

    private static double ScreenY(CanvasView view, double hostY, double viewport) =>
        (hostY + CanvasViewport.Pad(viewport, view.Zoom)) * view.Zoom - view.OffsetY;

    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void ZoomKeepsTheAnchorStill(double target)
    {
        const double w = 800, h = 300;
        var start = CanvasViewport.Fit(1, w, h, Vw, Vh);
        var (px, py) = (420.0, 250.0);
        var hostX = CanvasViewport.HostAt(start.OffsetX, px, start.Zoom, CanvasViewport.Pad(Vw, start.Zoom));
        var hostY = CanvasViewport.HostAt(start.OffsetY, py, start.Zoom, CanvasViewport.Pad(Vh, start.Zoom));

        var view = CanvasViewport.ZoomAt(start, target, px, py, w, h, Vw, Vh);

        Assert.Equal(target, view.Zoom, 6);
        Assert.Equal(px, ScreenX(view, hostX, Vw), 6);
        Assert.Equal(py, ScreenY(view, hostY, Vh), 6);
    }

    [Fact]
    public void ZoomSurvivesRoundTrips()
    {
        var view = CanvasViewport.Fit(1, 800, 300, Vw, Vh);
        var hostX = CanvasViewport.HostAt(view.OffsetX, 130, view.Zoom, CanvasViewport.Pad(Vw, view.Zoom));
        for (var i = 0; i < 5; i++)
            view = CanvasViewport.ZoomAt(view, view.Zoom * 1.1, 130, 200, 800, 300, Vw, Vh);
        for (var i = 0; i < 5; i++)
            view = CanvasViewport.ZoomAt(view, view.Zoom / 1.1, 130, 200, 800, 300, Vw, Vh);
        Assert.Equal(1, view.Zoom, 6);
        Assert.Equal(130, ScreenX(view, hostX, Vw), 6);
    }

    [Theory]
    [InlineData(0.1, 0.5)]
    [InlineData(0.5, 0.5)]
    [InlineData(2.0, 2.0)]
    [InlineData(9.0, 2.0)]
    public void ZoomClampsAtBothEnds(double asked, double expected)
    {
        var start = new CanvasView(0, 0, 1);
        Assert.Equal(expected, CanvasViewport.ZoomAt(start, asked, 500, 300, 800, 300, Vw, Vh).Zoom, 6);
    }

    [Fact]
    public void NotchesMultiplyByATenthEach()
    {
        Assert.Equal(1.1, CanvasViewport.WheelFactor(120), 6);
        Assert.Equal(1 / 1.1, CanvasViewport.WheelFactor(-120), 6);
        Assert.Equal(1.21, CanvasViewport.WheelFactor(240), 6);
    }

    [Theory]
    [InlineData(200, 100, 1.0)] // a flow smaller than the window
    [InlineData(3000, 1500, 1.0)] // a flow larger than the window
    [InlineData(200, 100, 2.0)]
    [InlineData(3000, 1500, 0.5)]
    public void PanRoomKeeps48DipOnScreen(double w, double h, double zoom)
    {
        var padX = CanvasViewport.Pad(Vw, zoom);
        var maxX = CanvasViewport.MaxOffset(w, padX, zoom, Vw);
        var inset = CanvasViewport.Inset; // the layout's margin around the steps
        // Scrolled to the start: the steps' left edge sits 48 DIP from the window's right edge.
        Assert.Equal(Vw - 48, ScreenX(new CanvasView(0, 0, zoom), inset, Vw), 6);
        // Scrolled to the end: the steps' right edge sits 48 DIP from the window's left edge.
        Assert.Equal(48, ScreenX(new CanvasView(maxX, 0, zoom), w - inset, Vw), 6);

        var maxY = CanvasViewport.MaxOffset(h, CanvasViewport.Pad(Vh, zoom), zoom, Vh);
        Assert.Equal(Vh - 48, ScreenY(new CanvasView(0, 0, zoom), inset, Vh), 6);
        Assert.Equal(48, ScreenY(new CanvasView(0, maxY, zoom), h - inset, Vh), 6);
    }

    [Fact]
    public void FitCentresAFlowThatFits()
    {
        var view = CanvasViewport.Fit(1, 400, 200, Vw, Vh);
        Assert.Equal(Vw / 2, ScreenX(view, 200, Vw), 6);
        Assert.Equal(Vh / 2, ScreenY(view, 100, Vh), 6);
    }

    [Fact]
    public void FitShowsTheStartOfAFlowTooBigAtTheSmallestZoom()
    {
        var view = CanvasViewport.Fit(0.5, 4000, 200, Vw, Vh);
        Assert.Equal(0, ScreenX(view, 0, Vw), 6);
        Assert.Equal(Vh / 2, ScreenY(view, 100, Vh), 6);
    }

    [Fact]
    public void ATinyViewportHasNoPanRoom()
    {
        Assert.Equal(0, CanvasViewport.Pad(30, 1));
        Assert.Equal(0, CanvasViewport.MaxOffset(10, 0, 1, 30));
    }
}
