using DazeControl.Engine.Shell;
using DazeControl.Runtime.Lifecycle;
using Velopack;
using Windows.Win32;

namespace DazeControl.Engine.Lifecycle;

/// <summary>
/// Velopack's install/update/uninstall hooks (D-043, OSD D-029). Setup, the updater and uninstall run the engine exe
/// with a hook argument; <c>Run()</c> handles it and exits the process. Outside an install it returns at once. Runs
/// before the single-instance check and the log: a hook runs while the real engine may be running, and must not write
/// its log. Velopack keeps its own log of the hooks (<c>%LOCALAPPDATA%\velopack</c>).
/// </summary>
internal static class Installer
{
    /// <summary>Hooks get 15 s; the rest is Velopack's.</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);

    public static void RunHooks() =>
        VelopackApp.Build()
            // Its own apply-on-start would also restart with whatever args this start had; Program applies instead.
            .SetAutoApplyOnStartup(false)
            // The old version, before the updater kills what runs from the install: let a running engine exit cleanly
            // (tray icon removed, runs cancelled, history written).
            .OnBeforeUpdateFastCallback(_ => StopRunningEngine())
            // Uninstall has already killed the engine by now (velopack log, 2026-11-02): only clean up after it.
            // Only a Run value that starts this install; a Debug copy's entry isn't ours to remove.
            .OnBeforeUninstallFastCallback(_ =>
            {
                new Autostart().DisableIfUnder(AppContext.BaseDirectory);
                RemoveDesktopShortcuts();
            })
            .Run();

    /// <summary>Our desktop shortcuts (any config) and their icons (D-049). Uninstall goes on if Windows refuses.</summary>
    private static void RemoveDesktopShortcuts()
    {
        try
        {
            FlowShortcuts.Create(null, EnginePaths.DefaultConfig).RemoveAll();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // Velopack logs a hook's output; our own log must not be written from a hook.
            Console.Error.WriteLine($"desktop shortcuts not removed: {e.Message}");
        }
    }

    /// <summary>WM_CLOSE to the engine's window (tray Exit's path), then waits for its single-instance mutex.</summary>
    private static void StopRunningEngine()
    {
        var window = PInvoke.FindWindow(MessageWindow.ClassName, null);
        if (window.IsNull)
            return;
        PInvoke.PostMessage(window, PInvoke.WM_CLOSE, default, default);
        var deadline = DateTime.UtcNow + ExitWait;
        while (DateTime.UtcNow < deadline)
        {
            using var instance = SingleInstance.TryAcquire();
            if (instance is not null)
                return;
            Thread.Sleep(200);
        }
    }
}
