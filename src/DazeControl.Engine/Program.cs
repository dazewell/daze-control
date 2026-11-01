using DazeControl.Core;
using DazeControl.Core.Input;
using DazeControl.Core.Model;
using DazeControl.Core.Validation;
using DazeControl.Core.Yaml;
using DazeControl.Engine.Diagnostics;
using DazeControl.Engine.Shell;

namespace DazeControl.Engine;

/// <summary>
/// T0.5 AOT spike: load config.yaml through the YamlDotNet static context, show the tray icon, register each
/// flow's hotkeys, idle on the message loop. The real composition root arrives in T3.1.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Log.Initialize(Log.DefaultDirectory);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("unhandled", (Exception)e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) => Log.Error("unobserved task", e.Exception);
        Log.Info($"engine: start {typeof(Program).Assembly.GetName().Version}");

        var (config, rejected) = LoadConfig(args.Length > 0 ? args[0] : DefaultConfigPath());

        using var window = new MessageWindow();
        using var tray = new TrayIcon(window, AppInfo.ProductName);
        tray.ExitRequested += window.Close;
        using var hotkeys = new GlobalHotkey(window);

        var flowsById = new Dictionary<int, string>();
        foreach (var flow in config.Flows.Where(f => f.Enabled && !rejected.Contains(f.Id)))
        {
            var key = flow.Id;
            foreach (var text in flow.Hotkeys)
            {
                if (!Hotkey.TryParse(text, out var hotkey))
                {
                    Log.Info($"flow {key}: \"{text}\" is not a valid shortcut");
                    continue;
                }
                var id = flowsById.Count + 1;
                if (hotkeys.Register(id, hotkey) is null)
                {
                    flowsById[id] = key;
                    Log.Info($"flow {key}: hotkey {hotkey} registered");
                }
            }
        }
        hotkeys.Pressed += id => Log.Info($"hotkey pressed: flow {flowsById[id]}");

        Log.Info("engine: ready");
        MessageWindow.RunMessageLoop();
        Log.Info("engine: exit");
        return 0;
    }

    private static string DefaultConfigPath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.FolderName, "config.yaml");

    // Fail-soft: a missing or broken file means no flows, never a dead engine; a flow with errors is skipped
    // alone (D-021; keeping its last valid version arrives with ConfigHost in T2.6).
    private static (Configuration Config, IReadOnlySet<string> Rejected) LoadConfig(string path)
    {
        try
        {
            var load = ConfigYaml.Load(File.ReadAllText(path));
            var config = load.Config ?? Configuration.Empty;
            var problems = load.Problems.Concat(ConfigValidator.Validate(config, load.Problems)).ToList();
            foreach (var problem in problems)
                Log.Info($"config: {problem}");
            var rejected = ConfigValidator.FlowsWithErrors(problems);
            Log.Info($"config: {path}, {config.Flows.Count} flow(s), {rejected.Count} with errors");
            return (config, rejected);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Info($"config: {path} not readable ({e.Message}); no flows");
        }
        return (Configuration.Empty, new HashSet<string>());
    }
}
