using DazeControl.Editor.ViewModels;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Engine;
using Microsoft.UI.Xaml;

namespace DazeControl.Editor;

/// <summary>Runs only while its window is open: closing it ends the process (ARCHITECTURE → Footprint).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001", Justification = "Disposed when the window closes, which ends the app.")]
public partial class App : Application
{
    private ShellWindow? _window;
    private EngineClient? _engine;

    public App()
    {
        InitializeComponent();
        Instance = this;
    }

    public static App? Instance { get; private set; }

    public static string LogsFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Core.AppInfo.FolderName, "logs");

    /// <summary>A second start of the editor (any thread).</summary>
    public void BringToFront() => _window?.DispatcherQueue.TryEnqueue(() => _window?.BringToFront());

    /// <summary><see cref="Program.ConfigPath"/> edits another file; its backups go next to it.</summary>
    private static ConfigStore OpenStore() => Program.ConfigPath is { } path
        ? new ConfigStore(path, Path.Combine(Path.GetDirectoryName(path)!, "backups"))
        : new ConfigStore(ConfigStore.DefaultPath, ConfigStore.DefaultBackupDirectory);

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        void Post(Action action) => ui.TryEnqueue(() => action());

        _engine = new EngineClient();
        var session = new ConfigSession(OpenStore(), TimeProvider.System, Post);
        session.Load();
        var shell = new ShellViewModel(session, _engine, Post, LogsFolder);
        _window = new ShellWindow(shell);
        _window.Closed += (_, _) =>
        {
            shell.Dispose();
            session.Dispose(); // writes pending edits
            _ = _engine.DisposeAsync().AsTask();
        };
        _engine.Start();
        _window.Activate();
    }
}
