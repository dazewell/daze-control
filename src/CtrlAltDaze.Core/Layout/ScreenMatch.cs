namespace CtrlAltDaze.Core.Layout;

/// <summary>
/// Which plugged-in screen a <c>display.outputs</c> entry means (D-051): its port or exact monitor name first, and
/// only then a monitor name that contains it, so "DELL U2723QE" can't pick "DELL U2723QE HDR" while the exact one
/// is there. The provider and the editor's checklist share it.
/// </summary>
public static class ScreenMatch
{
    /// <summary>The index of the screen <paramref name="entry"/> means, or -1.</summary>
    public static int IndexOf<T>(string entry, IReadOnlyList<T> screens, Func<T, string> port, Func<T, string> monitor)
    {
        for (var i = 0; i < screens.Count; i++)
        {
            if (port(screens[i]).Equals(entry, StringComparison.OrdinalIgnoreCase)
                || monitor(screens[i]).Equals(entry, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        for (var i = 0; i < screens.Count; i++)
        {
            if (monitor(screens[i]) is { Length: > 0 } name && name.Contains(entry, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return -1;
    }
}
