namespace DazeControl.Editor.ViewModels.Canvas;

/// <summary>What a press on the canvas starts, as far as panning goes (UX §5.2, F-033).</summary>
public enum PanPress
{
    /// <summary>Not a pan: the other gestures (connect, drag, edge, marquee) or the touch manipulation decide.</summary>
    None,

    /// <summary>Middle-drag, or left-drag while Space is held: pans at once (the pointer is captured).</summary>
    Pan,

    /// <summary>A mouse right press: armed only; leaving the drag rectangle pans, a still click opens the menu (D-069).</summary>
    RightArm,

    /// <summary>A pen's barrel button or a touch right press: its menu comes from RightTapped, nothing else starts.</summary>
    Menu,
}

/// <summary>
/// The canvas's pan decisions, kept out of the view so they can be tested (F-033): which button and key start a pan,
/// whether Space counts, and when a gesture left over from a lost release must give way to a new press.
/// </summary>
public static class PanInput
{
    /// <summary>
    /// The pan a press starts. <paramref name="spaceHeld"/> is Space's state for the window's thread at the press, so
    /// Space works whichever element had keyboard focus when it went down (it used to count only when the canvas did).
    /// </summary>
    public static PanPress OnPress(bool left, bool middle, bool right, bool spaceHeld, bool isMouse, bool isTouch)
    {
        if (isTouch)
            return right ? PanPress.Menu : PanPress.None; // one finger pans through the manipulation
        if (middle || (spaceHeld && left))
            return PanPress.Pan;
        if (right)
            return isMouse ? PanPress.RightArm : PanPress.Menu;
        return PanPress.None;
    }

    /// <summary>
    /// Whether a gesture still running when a new press comes is a leftover that must end first: a right press only
    /// armed (its release went to a canvas button that handled it), or any gesture that no longer holds the pointer
    /// capture (released or lost outside the canvas). A live gesture holds the capture, so it is never cut short.
    /// </summary>
    public static bool IsStale(bool gestureRunning, bool armedOnly, bool hasCapture) =>
        gestureRunning && (armedOnly || !hasCapture);
}
