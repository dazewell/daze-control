using System.Runtime.InteropServices;

namespace DazeControl.Engine.Interop;

/// <summary>
/// A 1 ms system timer tick for as long as the owner holds it. Without it Windows rounds every wait and WM_TIMER up to its
/// 15.6 ms tick, so a "16 ms" frame or poll lands every 16 or 31 ms and the menu's focus slide looks like ~15-30 fps.
/// Held only while the menu polls the pad or animates, so idle stays zero (D-009). One owner per instance; Begin/End are idempotent.
/// </summary>
internal sealed partial class FineTimer
{
    private bool _held;

    public void Begin()
    {
        if (_held)
            return;
        _held = TimeBeginPeriod(1) == 0;
    }

    public void End()
    {
        if (!_held)
            return;
        _held = false;
        _ = TimeEndPeriod(1);
    }

    [LibraryImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static partial uint TimeBeginPeriod(uint period);

    [LibraryImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static partial uint TimeEndPeriod(uint period);
}
