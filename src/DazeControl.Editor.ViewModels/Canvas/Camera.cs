using DazeControl.Core.Layout;

namespace DazeControl.Editor.ViewModels.Canvas;

/// <summary>
/// The canvas camera (D-119 e): one transform from the flow's own coordinates ("world": the layout's DIP, what
/// <c>Host</c> lays out in) to the viewport ("screen": DIP from its top left): screen = world × <see cref="Zoom"/> +
/// (<see cref="X"/>, <see cref="Y"/>). The canvas applies it as a single render transform, so a pan or zoom never
/// runs a layout pass; the pan is unbounded except for <see cref="Clamp"/>, which keeps <see cref="Keep"/> DIP of the
/// steps on screen (D-074).
/// </summary>
public readonly record struct Camera(double X, double Y, double Zoom)
{
    /// <summary>How much of the steps stays on screen at the end of the pan room, in screen DIP.</summary>
    public const double Keep = 48;

    /// <summary>Shift+wheel and a tilt wheel: screen DIP panned per notch (120).</summary>
    public const double PanPerNotch = 96;

    /// <summary>A touchpad's two-finger scroll: screen DIP panned per wheel unit (its deltas follow the fingers).</summary>
    public const double TouchpadPanPerUnit = 1;

    /// <summary>A rect revealed by <see cref="Reveal"/> keeps this much screen DIP from the viewport's edges.</summary>
    public const double RevealMargin = 24;

    public static Camera Identity => new(0, 0, 1);

    /// <summary>The layout's margin around the steps inside the extent (world DIP): the pan room starts at the steps.</summary>
    public static double Inset => LayoutOptions.Default.Padding;

    public static double ClampZoom(double zoom) => Math.Clamp(zoom, FlowCanvasViewModel.MinZoom, FlowCanvasViewModel.MaxZoom);

    /// <summary>×1.1 per wheel notch (120); a touchpad pinch reports fractions of a notch.</summary>
    public static double WheelFactor(double delta) => Math.Pow(1.1, delta / 120.0);

    public Point ToScreen(Point world) => new(world.X * Zoom + X, world.Y * Zoom + Y);

    public Point ToWorld(Point screen) => new((screen.X - X) / Zoom, (screen.Y - Y) / Zoom);

    public Rect ToScreen(Rect world) => new(world.X * Zoom + X, world.Y * Zoom + Y, world.Width * Zoom, world.Height * Zoom);

    /// <summary>Moves the flow by (dx, dy) screen DIP.</summary>
    public Camera PanBy(double dx, double dy) => this with { X = X + dx, Y = Y + dy };

    /// <summary>Zoom to <paramref name="zoom"/> (clamped to 10–200 %, F-006), keeping the world point under (sx, sy) where it is.</summary>
    public Camera ZoomAt(double zoom, double sx, double sy)
    {
        zoom = ClampZoom(zoom);
        var world = ToWorld(new Point(sx, sy));
        return new Camera(sx - world.X * zoom, sy - world.Y * zoom, zoom);
    }

    /// <summary>
    /// The camera at <paramref name="zoom"/> (clamped) that centres a flow of that extent along each axis where it
    /// fits, and shows where it starts along an axis where it doesn't.
    /// </summary>
    public static Camera Fit(double zoom, double extentWidth, double extentHeight, double viewportWidth, double viewportHeight)
    {
        zoom = ClampZoom(zoom);
        return new Camera(Axis(extentWidth, viewportWidth), Axis(extentHeight, viewportHeight), zoom);

        double Axis(double extent, double viewport) => extent * zoom <= viewport ? (viewport - extent * zoom) / 2 : 0;
    }

    /// <summary>
    /// The nearest camera that keeps at least <see cref="Keep"/> screen DIP of the steps (the extent less the layout's
    /// <see cref="Inset"/>) on screen along each axis (D-074's pan room, now without a scroll extent).
    /// </summary>
    public Camera Clamp(double extentWidth, double extentHeight, double viewportWidth, double viewportHeight) =>
        new(ClampAxis(X, extentWidth, viewportWidth), ClampAxis(Y, extentHeight, viewportHeight), Zoom);

    private double ClampAxis(double offset, double extent, double viewport)
    {
        if (viewport <= 0 || extent <= 0)
            return offset;
        var min = Keep - (extent - Inset) * Zoom; // the steps' far edge Keep DIP inside the window's near edge
        var max = viewport - Keep - Inset * Zoom; // the steps' near edge Keep DIP inside the window's far edge
        return min <= max ? Math.Clamp(offset, min, max) : (min + max) / 2; // a window too small to keep both: centre
    }

    /// <summary>Auto-pan (T21.9): the band along each viewport edge, in screen DIP, where a drag, connect or marquee pans.</summary>
    public const double EdgeBand = 40;

    /// <summary>
    /// Auto-pan's top speed, in screen DIP per second: at the edge and beyond it (the pointer is captured). 600 as in
    /// Miro and Figma (F-001: 1200 felt like the flow ran away).
    /// </summary>
    public const double MaxEdgeSpeed = 600;

    /// <summary>
    /// How far past the steps auto-pan still goes, in world DIP: room for one more column (the widest pill and a column
    /// gap), so a step can be dragged just past the last one but the flow never scrolls away (F-001).
    /// </summary>
    public const double EdgeRoom = 320 + 64;

    /// <summary>A frame gap longer than this (a stall) pans as if it were this long, so the flow never jumps.</summary>
    public const double MaxEdgeStep = 1 / 30.0;

    /// <summary>
    /// How fast the flow pans with the pointer at screen (sx, sy): (0, 0) outside <see cref="EdgeBand"/>, else in the
    /// pointer's direction, linear in how deep it is in the band (no time-based acceleration: the same with Animation
    /// effects off), <see cref="MaxEdgeSpeed"/> at the edge and past it. Screen DIP per second, the way the pointer goes.
    /// </summary>
    public static (double X, double Y) EdgeVelocity(double sx, double sy, double viewportWidth, double viewportHeight) =>
        (EdgeAxis(sx, viewportWidth), EdgeAxis(sy, viewportHeight));

    private static double EdgeAxis(double at, double viewport)
    {
        if (viewport < 3 * EdgeBand) // too small to have a middle: never auto-pan
            return 0;
        if (at < EdgeBand)
            return -MaxEdgeSpeed * Math.Min(1, (EdgeBand - at) / EdgeBand);
        if (at > viewport - EdgeBand)
            return MaxEdgeSpeed * Math.Min(1, (at - (viewport - EdgeBand)) / EdgeBand);
        return 0;
    }

    /// <summary>
    /// <paramref name="velocity"/> with each axis stopped once the view already shows <see cref="EdgeRoom"/> past the
    /// <paramref name="content"/> (world: the steps that stay put) that way: auto-pan runs out with the content (F-001).
    /// </summary>
    public (double X, double Y) EdgeLimit((double X, double Y) velocity, Rect content, double viewportWidth, double viewportHeight)
    {
        if (content.Width <= 0 && content.Height <= 0)
            return velocity;
        var near = ToScreen(new Point(content.X - EdgeRoom, content.Y - EdgeRoom));
        var far = ToScreen(new Point(content.Right + EdgeRoom, content.Bottom + EdgeRoom));
        return (Axis(velocity.X, near.X, far.X, viewportWidth), Axis(velocity.Y, near.Y, far.Y, viewportHeight));

        static double Axis(double v, double near, double far, double viewport) =>
            v > 0 && far <= viewport ? 0 : v < 0 && near >= 0 ? 0 : v;
    }

    /// <summary>
    /// One auto-pan frame of <paramref name="seconds"/> (capped at <see cref="MaxEdgeStep"/>): shows more of the flow
    /// the way the pointer goes, so the world point under the still pointer moves that way.
    /// </summary>
    public Camera EdgePan((double X, double Y) velocity, double seconds)
    {
        var dt = Math.Clamp(seconds, 0, MaxEdgeStep);
        return PanBy(-velocity.X * dt, -velocity.Y * dt);
    }

    /// <summary>
    /// The smallest pan that brings <paramref name="world"/> on screen with <see cref="RevealMargin"/> around it (keyboard
    /// focus, a new step); the start wins when it can't all fit.
    /// </summary>
    public Camera Reveal(Rect world, double viewportWidth, double viewportHeight)
    {
        var screen = ToScreen(world);
        return PanBy(Axis(screen.X, screen.Right, viewportWidth), Axis(screen.Y, screen.Bottom, viewportHeight));

        static double Axis(double start, double end, double viewport)
        {
            if (viewport <= 0)
                return 0;
            var shift = end + RevealMargin > viewport ? viewport - RevealMargin - end : 0;
            return start + shift < RevealMargin ? RevealMargin - start : shift;
        }
    }
}
