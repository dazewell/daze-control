using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace CtrlAltDaze.Editor;

/// <summary>
/// Own Main (DISABLE_XAML_GENERATED_MAIN) for single instance: a second start signals the first, which brings its
/// window to the front, and exits. A named mutex + event: simpler than AppInstance redirection, no async wait in Main.
/// </summary>
public static partial class Program
{
    private const int AsfwAny = -1;

    /// <summary><c>--config &lt;path&gt;</c>: the file this instance edits (tests, screenshots); null = the user's config.</summary>
    public static string? ConfigPath { get; private set; }

    /// <summary><c>--canvas-probe</c> (Debug builds only): the canvas logs its frame times per gesture (T21.8).</summary>
    public static bool CanvasProbe { get; private set; }

    /// <summary><c>--memory-probe &lt;flow id&gt; &lt;log&gt;</c>, only with <c>--config</c>: see ShellWindow.MemoryProbe (F-036).</summary>
    public static (string FlowId, string Log)? MemoryProbe { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
#if DEBUG
        CanvasProbe = args.Contains("--canvas-probe");
#endif
        var at = Array.IndexOf(args, "--config");
        if (at >= 0)
        {
            // A broken test command must never fall back to the user's real file.
            if (at + 1 >= args.Length || args[at + 1].StartsWith("--", StringComparison.Ordinal))
                return 2;
            ConfigPath = Path.GetFullPath(args[at + 1]);
        }
        else
        {
            // The rename (T29.2, D-152): the old folders are copied before anything reads the new ones.
            Core.OldSetup.CopyFolders();
        }
        var probe = Array.IndexOf(args, "--memory-probe");
        if (probe >= 0)
        {
            // It edits the open flow (and undoes it): never on the user's real file.
            if (ConfigPath is null || probe + 2 >= args.Length)
                return 2;
            MemoryProbe = (args[probe + 1], Path.GetFullPath(args[probe + 2]));
        }

        // Session-local names: one editor per signed-in user and file, so a --config instance never hands its
        // launch to the editor of the user's config (or the other way round).
        var scope = ConfigPath is null ? "" : "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ConfigPath.ToUpperInvariant())))[..16];
        using var instance = new Mutex(initiallyOwned: true, @"Local\CtrlAltDaze.Editor" + scope, out var first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\CtrlAltDaze.Editor.Show" + scope);
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
        return Environment.ExitCode; // 0 unless the memory probe failed
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
