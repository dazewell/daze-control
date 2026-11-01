using DazeControl.Engine.Diagnostics;
using DazeControl.Engine.Lifecycle;
using DazeControl.Runtime.Providers;

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
        Log.Initialize(Log.DefaultDirectory);
        // Fail-soft: these only log; an unobserved task must never end the process (CLAUDE.md).
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled", (Exception)e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("unobserved task", e.Exception);
            e.SetObserved();
        };
        Log.Info($"engine: start {typeof(Program).Assembly.GetName().Version}");

        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            Log.Info("engine: another engine is running in this session; exiting");
            return 0;
        }

        var paths = EnginePaths.Default(args.Length > 0 ? Path.GetFullPath(args[0]) : null);
        Log.Info($"engine: config {paths.Config}");
        // Providers arrive in M4; until then every step fails with "This version can't run … steps".
        using var host = new EngineHost(paths, Array.Empty<IStepProvider>());
        host.Run();
        Log.Info("engine: exit");
        return 0;
    }
}
