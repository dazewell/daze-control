using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace DazeControl.Editor;

/// <summary>
/// Own Main (DISABLE_XAML_GENERATED_MAIN) for single instance: a second start signals the first, which brings its
/// window to the front, and exits. A named mutex + event, not AppInstance redirection (X-002).
/// </summary>
public static partial class Program
{
    private const int AsfwAny = -1;

    [STAThread]
    public static int Main()
    {
        // Session-local names: one editor per signed-in user.
        using var instance = new Mutex(initiallyOwned: true, @"Local\DazeControl.Editor", out var first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\DazeControl.Editor.Show");
        if (!first)
        {
            // Our process may take the foreground (the engine or Explorer started it); pass that on.
            _ = AllowSetForegroundWindow(AsfwAny);
            show.Set();
            return 0;
        }

        var watcher = new Thread(() =>
        {
            while (show.WaitOne())
                App.Instance?.BringToFront();
        }) { IsBackground = true, Name = "editor show" };
        watcher.Start();

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(start =>
        {
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
            _ = new App();
        });
        return 0;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
