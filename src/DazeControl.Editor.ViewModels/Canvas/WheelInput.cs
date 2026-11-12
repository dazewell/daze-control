namespace DazeControl.Editor.ViewModels.Canvas;

/// <summary>What one wheel event does on the canvas.</summary>
public enum WheelAction { Zoom, PanVertical, PanSideways }

/// <summary>
/// Tells a precision touchpad's two-finger scroll from a mouse wheel (D-119 e). Windows reports touchpad vertical scroll
/// as plain mouse wheel input (live, 2026-11-12: up/down zoomed, left/right panned), so the device type alone can't tell
/// them apart. A wheel event is the touchpad's when Windows says so (the input message source), or when its delta isn't a
/// whole notch (120): a touchpad reports small deltas that follow the fingers, a notched wheel whole ones. A swipe stays the
/// touchpad's for <see cref="StickyMicroseconds"/> after its last event, so a whole-notch delta inside it doesn't zoom.
/// Ctrl always zooms (a touchpad pinch arrives as Ctrl+wheel).
/// </summary>
public sealed class WheelInput
{
    public const int Notch = 120;

    /// <summary>How long a touchpad swipe keeps its later events, from its last one.</summary>
    public const ulong StickyMicroseconds = 300_000;

    private ulong? _lastTouchpad;

    /// <summary>True when the last classified event was the touchpad's.</summary>
    public bool LastWasTouchpad { get; private set; }

    /// <param name="delta">The wheel delta (120 per notch).</param>
    /// <param name="horizontal">A horizontal wheel (tilt, or a sideways swipe).</param>
    /// <param name="ctrl">Ctrl is down (or added by the system for a pinch).</param>
    /// <param name="shift">Shift is down.</param>
    /// <param name="timestamp">The event's timestamp, in microseconds.</param>
    /// <param name="fromTouchpad">Windows reported the event as the touchpad's (device type or message source).</param>
    public WheelAction Classify(int delta, bool horizontal, bool ctrl, bool shift, ulong timestamp, bool fromTouchpad)
    {
        var touchpad = fromTouchpad || delta % Notch != 0
            || (_lastTouchpad is { } last && timestamp >= last && timestamp - last <= StickyMicroseconds);
        if (touchpad)
            _lastTouchpad = timestamp;
        LastWasTouchpad = touchpad;
        if (ctrl)
            return WheelAction.Zoom;
        if (horizontal)
            return WheelAction.PanSideways;
        if (touchpad)
            return WheelAction.PanVertical;
        return shift ? WheelAction.PanSideways : WheelAction.Zoom;
    }
}
