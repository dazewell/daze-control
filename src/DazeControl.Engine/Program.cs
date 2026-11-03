using DazeControl.Core.Updates;
using DazeControl.Engine.Diagnostics;
using DazeControl.Engine.Lifecycle;
using DazeControl.Ipc;
using DazeControl.Providers;
using DazeControl.Runtime.Lifecycle;

namespace DazeControl.Engine;

/// <summary>
/// Engine entry: installer hooks, single instance, log, global handlers, a pending update, then <see cref="EngineHost"/>.
/// Usage: <c>DazeControl.exe [--run &lt;flow id&gt;] [config.yaml]</c> (the default config is
/// <c>%APPDATA%\DazeControl\config.yaml</c>; <c>--run</c> comes from a desktop shortcut, D-049).
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Read before the hooks: VelopackApp.Run() clears it.
        var restartedByUpdater = VelopackUpdateClient.RestartedByUpdater();
        Installer.RunHooks();
        var arguments = EngineArguments.Parse(args);
        // Before the log: a second engine must not write (or rotate) the running engine's log.
        using var instance = SingleInstance.TryAcquire();
        if (instance is null)
        {
            if (arguments.RunFlowId is { } flowId)
                ForwardRun(flowId, ConfigPathOf(arguments) ?? EnginePaths.DefaultConfig);
            return 0;
        }
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

        var paths = EnginePaths.Default(ConfigPathOf(arguments));
        Log.Info($"engine: config {paths.Config}");
        using var host = new EngineHost(paths, notifier => BuiltInProviders.Create(TimeProvider.System, notifier), BuiltInProviders.Devices());
        using var updates = installed
            ? new UpdateCheck(Path.Combine(AppContext.BaseDirectory, "dctl.exe"), TimeProvider.System)
            : null;
        host.Run(arguments.RunFlowId);
        Log.Info("engine: exit");
        return 0;
    }

    /// <summary>How long a shortcut's second engine waits for the first one's pipe (it may be starting up).</summary>
    private static readonly TimeSpan ForwardTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A desktop shortcut while the engine runs (D-049): hand the run to it and exit. Writes nothing (the log is the
    /// running engine's); a run that can't start is the running engine's pop-up.
    /// </summary>
    private static string? ConfigPathOf(EngineArguments arguments) =>
        arguments.Config is { } config ? Path.GetFullPath(config) : null;

    private static void ForwardRun(string flowId, string configPath)
    {
        try
        {
            Task.Run(async () =>
            {
                using var cancel = new CancellationTokenSource(ForwardTimeout);
                var client = await PipeClient.ConnectAsync(Protocol.CurrentPipeName(), ForwardTimeout, cancel.Token).ConfigureAwait(false);
                if (client is null)
                    return;
                await using (client.ConfigureAwait(false))
                    await client.SendAsync(EngineHost.DesktopRun(flowId, configPath), cancel.Token).ConfigureAwait(false);
            }).Wait();
        }
        catch (AggregateException)
        {
            // Nothing to tell: an engine that went away or answered late shows nothing either way.
        }
    }
}
