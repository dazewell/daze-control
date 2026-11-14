namespace CtrlAltDaze.Runtime.Triggers;

/// <summary>
/// Turns the two power-setting readings into <c>display.off/on</c> and <c>session.away/back</c> (T24.4). Windows sends
/// the current value once on registration; that first reading is the baseline, never an event, so only a change
/// raises. Dimmed counts as "on" (nothing changes for the user), every presence value but 0 counts as "away".
/// Windows calls on its own thread; thread-safe.
/// </summary>
public sealed class ScreenAwayDetector
{
    private readonly Lock _lock = new();
    private int _off = -1; // -1 = no baseline yet; 1 = the screens are off, 0 = on or dimmed
    private int _away = -1; // -1 = no baseline yet; 1 = away, 0 = at the PC

    /// <summary>Forgets both readings: the next ones are baselines (the source was unloaded and loaded again).</summary>
    public void Reset()
    {
        lock (_lock)
            (_off, _away) = (-1, -1);
    }

    /// <summary>GUID_CONSOLE_DISPLAY_STATE (0 off, 1 on, 2 dimmed). Returns the event to raise, or null.</summary>
    public string? Display(int state)
    {
        var off = state == 0 ? 1 : 0;
        lock (_lock)
        {
            var previous = _off;
            _off = off;
            return previous == -1 || previous == off ? null : off == 1 ? "display.off" : "display.on";
        }
    }

    /// <summary>GUID_SESSION_USER_PRESENCE (0 present, 1 not present, 2 inactive). Returns the event to raise, or null.</summary>
    public string? Presence(int state)
    {
        var away = state == 0 ? 0 : 1;
        lock (_lock)
        {
            var previous = _away;
            _away = away;
            return previous == -1 || previous == away ? null : away == 1 ? "session.away" : "session.back";
        }
    }
}
