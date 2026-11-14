namespace CtrlAltDaze.Runtime.Triggers;

/// <summary>
/// Decides when "the PC woke up" (power.resume, schedule re-arm). Classic S3 sends PBT_APMRESUMEAUTOMATIC; Modern
/// Standby (Kernel-Power 506/507) sends neither that nor APMSUSPEND to apps, only the console display state going
/// off and then on again. So: a display-on after a display-off counts as a resume, a classic resume counts too, and
/// both signals for one wake-up (S3 sends both) collapse into one event inside <see cref="Window"/>. The first
/// display reading after registration is the baseline, never an event. Windows calls on its own thread; thread-safe.
/// Known cost: a display-timeout wake (screen off, then mouse moved) also looks like this exit.
/// </summary>
public sealed class ResumeDetector(TimeProvider time, Action resumed)
{
    /// <summary>One wake-up produces several signals inside this span; they raise once.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    private readonly Lock _lock = new();
    private int _display = -1; // -1 = no baseline yet; 0 off, 1 on, 2 dimmed
    private long _last;
    private bool _raisedBefore;

    /// <summary>Forgets the display state: the next <see cref="DisplayState"/> is a baseline.</summary>
    public void Reset()
    {
        lock (_lock)
            _display = -1;
    }

    /// <summary>PBT_APMRESUMEAUTOMATIC / PBT_APMRESUMESUSPEND.</summary>
    public void ResumeAutomatic() => Raise();

    /// <summary>GUID_CONSOLE_DISPLAY_STATE value (0 off, 1 on, 2 dimmed).</summary>
    public void DisplayState(int state)
    {
        bool wake;
        lock (_lock)
        {
            wake = _display == 0 && state != 0;
            _display = state;
        }
        if (wake)
            Raise();
    }

    private void Raise()
    {
        lock (_lock)
        {
            var now = time.GetTimestamp();
            if (_raisedBefore && time.GetElapsedTime(_last, now) < Window)
                return;
            (_last, _raisedBefore) = (now, true);
        }
        resumed();
    }
}
