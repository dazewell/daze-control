namespace CtrlAltDaze.Runtime.Lifecycle;

/// <summary>
/// The engine from before the rename (T29.2, D-152) still running would hold the same hotkeys and run the same flows.
/// At start, before hotkeys: when its single-instance mutex exists, ask it to exit and wait on the mutex (no polling).
/// </summary>
public static class OldEngine
{
    public static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The old app's own uninstaller, started and not waited for (T29.2 e: the app removes the old install itself, no
    /// prompt). Null when it is already gone; the next start tries again if it didn't finish.
    /// </summary>
    /// <param name="start">Starts an exe with arguments, hidden.</param>
    public static string? RemoveInstall(string updateExe, Action<string, string> start)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (!File.Exists(updateExe))
            return null;
        start(updateExe, "uninstall --silent");
        return "setup: removing the old app";
    }

    /// <summary>A line for the log, or null when no old engine was running.</summary>
    /// <param name="requestExit">Asks it to exit (the engine posts WM_CLOSE to its tray window, tray Exit's path).</param>
    public static string? Stop(string mutexName, Action requestExit, TimeSpan wait)
    {
        ArgumentNullException.ThrowIfNull(requestExit);
        if (!Mutex.TryOpenExisting(mutexName, out var mutex))
            return null;
        using (mutex)
        {
            requestExit();
            bool owned;
            try
            {
                owned = mutex.WaitOne(wait);
            }
            catch (AbandonedMutexException)
            {
                owned = true; // it was killed or crashed on the way out
            }
            if (!owned)
                return $"setup: the old engine didn't exit within {wait.TotalSeconds:0} s";
            mutex.ReleaseMutex();
            return "setup: the old engine exited";
        }
    }
}
