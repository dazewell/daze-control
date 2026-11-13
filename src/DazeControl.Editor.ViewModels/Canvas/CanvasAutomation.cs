using DazeControl.Core.Layout;

namespace DazeControl.Editor.ViewModels.Canvas;

/// <summary>
/// Where screen readers and UI Automation see a step (F-043): its world rect through the camera, placed in the
/// viewport's own screen rect. The viewport's automation rect is in the units UI Automation uses (screen pixels), so
/// its width over the viewport's width in DIP gives the scale, whatever the display scaling.
/// </summary>
public static class CanvasAutomation
{
    /// <summary>The screen rect of <paramref name="world"/> under <paramref name="camera"/>.</summary>
    public static Rect ScreenRect(Camera camera, Rect world, Rect viewportScreen, double viewportWidthDip)
    {
        var scale = viewportWidthDip > 0 ? viewportScreen.Width / viewportWidthDip : 1;
        var at = camera.ToScreen(new Point(world.X, world.Y));
        return new Rect(viewportScreen.X + at.X * scale, viewportScreen.Y + at.Y * scale,
            world.Width * camera.Zoom * scale, world.Height * camera.Zoom * scale);
    }

    /// <summary>A step panned or zoomed wholly out of the viewport is off screen (the viewport clips it).</summary>
    public static bool IsOffscreen(Rect screen, Rect viewportScreen) =>
        screen.Right <= viewportScreen.X || screen.X >= viewportScreen.Right
        || screen.Bottom <= viewportScreen.Y || screen.Y >= viewportScreen.Bottom;
}
