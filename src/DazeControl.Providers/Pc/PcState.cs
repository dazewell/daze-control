using System.Runtime.InteropServices;

namespace DazeControl.Providers.Pc;

/// <summary>Power as Windows reports it: plugged in (null = unknown) and the battery's charge (null = no battery or unknown).</summary>
public readonly record struct PowerReading(bool? PluggedIn, int? BatteryPercent);

/// <summary>The PC's own state for the checks (D-118), behind a seam (D-033). Each member is one read when asked; nothing is held.</summary>
public interface IPcState
{
    PowerReading Power();

    /// <summary>Time since the last keyboard or mouse input in this session.</summary>
    TimeSpan IdleFor();

    /// <summary>The session is locked; null = Windows didn't say.</summary>
    bool? Locked();

    /// <summary>Screens showing the desktop now.</summary>
    int ScreenCount();

    /// <summary>Free bytes the user can use on the drive holding <paramref name="path"/>.</summary>
    /// <exception cref="IOException">No such drive or it isn't ready.</exception>
    long FreeBytes(string path);
}

public sealed partial class WindowsPcState : IPcState
{
    public PowerReading Power()
    {
        if (!GetSystemPowerStatus(out var status))
            return new PowerReading(null, null);
        bool? plugged = status.AcLineStatus switch { 0 => false, 1 => true, _ => null };
        // BatteryFlag 128 = no system battery, 255 = unknown; percent 255 = unknown.
        int? percent = status.BatteryFlag is 128 or 255 || status.BatteryLifePercent > 100 ? null : status.BatteryLifePercent;
        return new PowerReading(plugged, percent);
    }

    public TimeSpan IdleFor()
    {
        var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref info))
            throw new InvalidOperationException("Windows didn't say when the last input was.");
        // Both are 32-bit tick counts: the unsigned difference survives the 49-day wrap.
        return TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.Time));
    }

    public unsafe bool? Locked()
    {
        // WTSSessionInfoEx (25) → WTSINFOEXW: Level (4) + 4 padding (the union is 8-aligned), then WTSINFOEX_LEVEL1_W: SessionId (8), SessionState (12, the connect state: 0 = active, NOT lock), SessionFlags (16: 0 lock, 1 unlock; X-017).
        if (!WTSQuerySessionInformationW(0, -1 /* this session */, 25, out var buffer, out var size) || buffer == 0)
            return null;
        try
        {
            if (size < 20)
                return null;
            return FromSessionFlags(*(int*)(buffer + 16));
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    /// <summary>WTSINFOEX_LEVEL1 SessionFlags: 0 = WTS_SESSIONSTATE_LOCK, 1 = UNLOCK (the documented values hold on 10.0.26300).</summary>
    internal static bool? FromSessionFlags(int flags) => flags switch { 0 => true, 1 => false, _ => null };

    public int ScreenCount() => GetSystemMetrics(80 /* SM_CMONITORS */);

    public long FreeBytes(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
            throw new IOException($"{path} isn't on a drive.");
        var drive = new DriveInfo(root);
        if (!drive.IsReady)
            throw new IOException($"{root} isn't ready.");
        return drive.AvailableFreeSpace;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerStatus
    {
        public byte AcLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint Size, Time;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetSystemPowerStatus(out PowerStatus status);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetLastInputInfo(ref LastInputInfo info);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("wtsapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WTSQuerySessionInformationW(nint server, int sessionId, int infoClass, out nint buffer, out uint bytes);

    [LibraryImport("wtsapi32.dll")]
    private static partial void WTSFreeMemory(nint memory);
}
