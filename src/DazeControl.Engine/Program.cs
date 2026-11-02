using DazeControl.Core.Updates;
using DazeControl.Engine.Diagnostics;
using DazeControl.Engine.Lifecycle;
using DazeControl.Providers;

namespace DazeControl.Engine;

/// <summary>
/// Engine entry: installer hooks, single instance, log, global handlers, a pending update, then <see cref="EngineHost"/>.
/// Usage: <c>DazeControl.exe [config.yaml]</c> (the default is <c>%APPDATA%\DazeControl\config.yaml</c>).
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Read before the hooks: VelopackApp.Run() clears it.
        var restartedByUpdater = VelopackUpdateClient.RestartedByUpdater();
        Installer.RunHooks();
        // Before the log: a second engine must not write (or rotate) the running engine's log.
        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
            return 0;
        Log.Initialize(Log.DefaultDirectory);
        // Fail-soft: these only log; an unobserved task must never end the process (CLAUDE.md).
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled", (Exception)e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("unobserved task", e.Exception);
            e.SetObserved();
        };
        Log.Info($"engine: start {typeof(Program).Assembly.GetName().Version}");
        // A downloaded update applies here, before anything starts (D-043): the updater waits for this exit.
        bool installed;
        try
        {
            if (VelopackUpdateClient.ApplyPendingAtStart(restartedByUpdater, args))
            {
                Log.Info("engine: exit to apply a downloaded update");
                return 0;
            }
            installed = VelopackUpdateClient.IsInstalledCopy();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Fail-soft: a broken install or update must not keep the engine from starting.
            Log.Error("updates: couldn't look for a downloaded update", e);
            installed = false;
        }

        var paths = EnginePaths.Default(args.Length > 0 ? Path.GetFullPath(args[0]) : null);
        Log.Info($"engine: config {paths.Config}");
        using var host = new EngineHost(paths, notifier => BuiltInProviders.Create(TimeProvider.System, notifier), BuiltInProviders.Devices());
        using var updates = installed
            ? new UpdateCheck(Path.Combine(AppContext.BaseDirectory, "dctl.exe"), TimeProvider.System)
            : null;
        host.Run();
        Log.Info("engine: exit");
        return 0;
    }
}
