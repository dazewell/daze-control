namespace DazeControl.Editor.ViewModels.Canvas;

/// <summary>
/// Tells a right-drag (pan) from a right click (menu) for the canvas (D-069). A right press arms; leaving the system
/// drag rectangle, centred on the press, starts the pan; the release after a pan swallows the one context menu that
/// follows. Positions and the rectangle are in DIP.
/// </summary>
public sealed class RightDragPan(double dragWidth, double dragHeight)
{
    private double _x, _y;
    private bool _swallowMenu;

    public bool IsArmed { get; private set; }

    public bool IsPanning { get; private set; }

    /// <summary>The system drag rectangle (SM_CXDRAG / SM_CYDRAG for <paramref name="dpi"/>, in pixels) in DIP.</summary>
    public static (double Width, double Height) DragRectDip(int cxPixels, int cyPixels, uint dpi)
    {
        var scale = 96.0 / (dpi == 0 ? 96 : dpi);
        return (cxPixels * scale, cyPixels * scale);
    }

    /// <summary>A right press at (x, y). Forgets a swallow that no menu request consumed.</summary>
    public void Arm(double x, double y)
    {
        (_x, _y) = (x, y);
        IsArmed = true;
        IsPanning = false;
        _swallowMenu = false;
    }

    /// <summary>True while the gesture pans (from the first move outside the drag rectangle until release).</summary>
    public bool Move(double x, double y)
    {
        if (!IsArmed)
            return false;
        if (!IsPanning && (Math.Abs(x - _x) > dragWidth / 2 || Math.Abs(y - _y) > dragHeight / 2))
            IsPanning = true;
        return IsPanning;
    }

    public void Release() => Cancel();

    /// <summary>Ends the gesture (release or lost capture); after a pan the next menu request is swallowed.</summary>
    public void Cancel()
    {
        if (!IsArmed)
            return; // a second end (capture lost after release) keeps the swallow
        _swallowMenu = IsPanning;
        IsArmed = IsPanning = false;
    }

    /// <summary>Called for each context-menu request: false (once) when it follows a pan.</summary>
    public bool TakeMenu()
    {
        if (!_swallowMenu)
            return true;
        _swallowMenu = false;
        return false;
    }
}
