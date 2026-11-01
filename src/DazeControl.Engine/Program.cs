using DazeControl.Engine.Diagnostics;
using DazeControl.Engine.Lifecycle;
using DazeControl.Providers;

namespace DazeControl.Engine;

/// <summary>
/// Engine entry: log, global handlers, single instance, then <see cref="EngineHost"/>. Usage:
/// <c>DazeControl.exe [config.yaml]</c> (the default is <c>%APPDATA%\DazeControl\config.yaml</c>).
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
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


        var paths = EnginePaths.Default(args.Length > 0 ? Path.GetFullPath(args[0]) : null);
        Log.Info($"engine: config {paths.Config}");
        using var host = new EngineHost(paths, notifier => BuiltInProviders.Create(TimeProvider.System, notifier));
        host.Run();
        Log.Info("engine: exit");
        return 0;
    }
}
