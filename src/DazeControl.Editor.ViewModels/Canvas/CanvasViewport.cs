namespace DazeControl.Editor.ViewModels.Canvas;

/// <summary>A canvas view: the ScrollViewer's offsets (zoomed DIP) and zoom factor.</summary>
public readonly record struct CanvasView(double OffsetX, double OffsetY, double Zoom);

/// <summary>
/// View maths for the canvas (D-074). The ScrollViewer's content is a wrapper whose padding on each side is
/// <see cref="Pad"/> (content DIP) around the flow, so a flow can be moved until only <see cref="Keep"/> DIP of its steps is
/// on screen. "Host" coordinates are the flow's own; "screen" positions are DIP from the viewport's top left.
/// </summary>
public static class CanvasViewport
{
    /// <summary>How much of the steps stays on screen at the end of the pan room, in screen DIP.</summary>
    public const double Keep = 48;

    /// <summary>The layout's margin around the steps inside the extent (content DIP): the pan room starts at the steps.</summary>
    public static double Inset => DazeControl.Core.Layout.LayoutOptions.Default.Padding;

    public static double Clamp(double zoom) => Math.Clamp(zoom, FlowCanvasViewModel.MinZoom, FlowCanvasViewModel.MaxZoom);

    /// <summary>Shift+wheel: screen DIP scrolled sideways per notch.</summary>
    public const double SideScrollPerNotch = 96;

    /// <summary>×1.1 per wheel notch (120).</summary>
    public static double WheelFactor(int delta) => Math.Pow(1.1, delta / 120.0);

    /// <summary>Wrapper padding per side along one axis, in content DIP, for that viewport size and zoom.</summary>
    public static double Pad(double viewport, double zoom) => Math.Max(0, (viewport - Keep) / zoom - Inset);

    /// <summary>The largest scroll offset along one axis for a flow of size <paramref name="extent"/>.</summary>
    public static double MaxOffset(double extent, double pad, double zoom, double viewport) =>
        Math.Max(0, (extent + 2 * pad) * zoom - viewport);

    /// <summary>The flow coordinate at screen position <paramref name="screen"/> along one axis.</summary>
    public static double HostAt(double offset, double screen, double zoom, double pad) => (offset + screen) / zoom - pad;

    /// <summary>The offset (clamped) that puts flow coordinate <paramref name="host"/> at <paramref name="screen"/>.</summary>
    public static double OffsetFor(double host, double screen, double extent, double viewport, double zoom)
    {
        var pad = Pad(viewport, zoom);
        return Math.Clamp((host + pad) * zoom - screen, 0, MaxOffset(extent, pad, zoom, viewport));
    }

    /// <summary>Zoom to <paramref name="zoom"/> (clamped), keeping the flow point under (sx, sy) where it is.</summary>
    public static CanvasView ZoomAt(CanvasView from, double zoom, double sx, double sy,
        double extentWidth, double extentHeight, double viewportWidth, double viewportHeight) =>
        Reanchor(from, Pad(viewportWidth, from.Zoom), Pad(viewportHeight, from.Zoom), Clamp(zoom), sx, sy,
            extentWidth, extentHeight, viewportWidth, viewportHeight);

    /// <summary>
    /// The view at <paramref name="zoom"/> that keeps the flow point under (sx, sy) still, given the view and the
    /// padding it was laid out with (the padding can be stale after a pinch or a resize).
    /// </summary>
    public static CanvasView Reanchor(CanvasView from, double padX, double padY, double zoom, double sx, double sy,
        double extentWidth, double extentHeight, double viewportWidth, double viewportHeight)
    {
        var hostX = HostAt(from.OffsetX, sx, from.Zoom, padX);
        var hostY = HostAt(from.OffsetY, sy, from.Zoom, padY);
        return new CanvasView(
            OffsetFor(hostX, sx, extentWidth, viewportWidth, zoom),
            OffsetFor(hostY, sy, extentHeight, viewportHeight, zoom),
            zoom);
    }

    /// <summary>The view at <paramref name="zoom"/> that centres the flow, or shows its start along an axis where it doesn't fit.</summary>
    public static CanvasView Fit(double zoom, double extentWidth, double extentHeight, double viewportWidth, double viewportHeight)
    {
        zoom = Clamp(zoom);
        return new CanvasView(Axis(extentWidth, viewportWidth), Axis(extentHeight, viewportHeight), zoom);

        double Axis(double extent, double viewport) => extent * zoom <= viewport
            ? OffsetFor(extent / 2, viewport / 2, extent, viewport, zoom)
            : OffsetFor(0, 0, extent, viewport, zoom);
    }
}
