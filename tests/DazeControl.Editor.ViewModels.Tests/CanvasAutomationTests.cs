using DazeControl.Core.Layout;
using DazeControl.Editor.ViewModels.Canvas;

namespace DazeControl.Editor.ViewModels.Tests;

/// <summary>UI Automation rects of canvas steps follow pan and zoom (F-043).</summary>
public sealed class CanvasAutomationTests
{
    private static readonly Rect Step = new(100, 50, 232, 56);

    // The viewport at screen (300, 200), 800 DIP wide, at 175 % display scale: 1400 px.
    private static readonly Rect Viewport = new(300, 200, 1400, 1050);

    [Fact]
    public void ThePeerRectTracksACameraChange()
    {
        var before = CanvasAutomation.ScreenRect(Camera.Identity, Step, Viewport, 800);
        Assert.Equal(new Rect(300 + 175, 200 + 87.5, 406, 98), before);

        var panned = CanvasAutomation.ScreenRect(Camera.Identity.PanBy(40, -20), Step, Viewport, 800);
        Assert.Equal(before.X + 40 * 1.75, panned.X, 6);
        Assert.Equal(before.Y - 20 * 1.75, panned.Y, 6);
        Assert.Equal(before.Width, panned.Width, 6);

        var zoomed = CanvasAutomation.ScreenRect(Camera.Identity.ZoomAt(0.5, 0, 0), Step, Viewport, 800);
        Assert.Equal(new Rect(300 + 87.5, 200 + 43.75, 203, 49), zoomed);
    }

    [Fact]
    public void AStepPannedOutOfTheViewportIsOffscreen()
    {
        var inView = CanvasAutomation.ScreenRect(Camera.Identity, Step, Viewport, 800);
        var away = CanvasAutomation.ScreenRect(Camera.Identity.PanBy(-400, 0), Step, Viewport, 800);
        var halfOut = CanvasAutomation.ScreenRect(Camera.Identity.PanBy(-200, 0), Step, Viewport, 800);

        Assert.False(CanvasAutomation.IsOffscreen(inView, Viewport));
        Assert.True(CanvasAutomation.IsOffscreen(away, Viewport));
        Assert.False(CanvasAutomation.IsOffscreen(halfOut, Viewport)); // partly shown still counts
    }
}
