using System.Runtime.InteropServices;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Display;

/// <summary>Monitor power behind a seam: the live call blanks the user's screens, so tests use a fake.</summary>
public interface IMonitorPower
{
    /// <summary>Puts every screen into power-off; the PC stays on and any input wakes it again.</summary>
    void TurnOff();
}

/// <summary><c>WM_SYSCOMMAND / SC_MONITORPOWER 2</c> posted to every top-level window: no admin, no focus, no window of ours.</summary>
public sealed partial class WindowsMonitorPower : IMonitorPower
{
    private const uint WmSysCommand = 0x0112;
    private const nint ScMonitorPower = 0xF170;
    private const nint MonitorOff = 2;
    private const nint HwndBroadcast = 0xFFFF;

    public void TurnOff()
    {
        // Posted, not sent: a hung window can't block the step.
        if (!PostMessageW(HwndBroadcast, WmSysCommand, ScMonitorPower, MonitorOff))
            throw new StepFailedException($"Windows refused to turn the screens off (error {Marshal.GetLastPInvokeError()}).");
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hWnd, uint message, nint wParam, nint lParam);
}

/// <summary>
/// <c>display.screensOff</c>: turns the screens off, the PC keeps running. A short guard delay first, so the key or
/// click that started the flow (its release counts as input) doesn't wake them again at once.
/// </summary>
public sealed class DisplayScreensOffProvider(IMonitorPower power, TimeProvider time) : IStepProvider
{
    internal static readonly TimeSpan Guard = TimeSpan.FromSeconds(1);

    public string Id => "display.screensOff";

    public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        await Task.Delay(Guard, time, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        power.TurnOff();
    }
}
