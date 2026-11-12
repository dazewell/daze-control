using DazeControl.Core.Backup;
using DazeControl.Editor.ViewModels;
using DazeControl.Editor.ViewModels.BackupCard;
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

    /// <summary>The editor window (owner of the file pickers); null before launch.</summary>
    public Microsoft.UI.Xaml.Window? MainWindow => _window;

    public static string LogsFolder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Core.AppInfo.FolderName, "logs");

    /// <summary>One line in logs\editor.log; never throws (the editor has no other log).</summary>
    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(LogsFolder);
            File.AppendAllText(Path.Combine(LogsFolder, "editor.log"),
                $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>One line in logs\editor.log from elsewhere in the editor (the canvas probe); never throws.</summary>
    internal static void LogLine(string message) => Log(message);

    /// <summary>A second start of the editor (any thread).</summary>
    public void BringToFront() => _window?.DispatcherQueue.TryEnqueue(() => _window?.BringToFront());

    /// <summary>Null when Velopack can't tell (fail-soft): the Updates card then says this copy doesn't update.</summary>
    private static Core.Updates.VelopackUpdateClient? CreateUpdateClient(Core.Updates.UpdateChannel channel)
    {
        try
        {
            return new Core.Updates.VelopackUpdateClient(channel);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary><see cref="Program.ConfigPath"/> edits another file; its backups go next to it, one folder per file.</summary>
    private static ConfigStore OpenStore() => Program.ConfigPath is { } path
        ? new ConfigStore(path, Path.Combine(Path.GetDirectoryName(path)!, "backups", Path.GetFileName(path)))
        : new ConfigStore(ConfigStore.DefaultPath, ConfigStore.DefaultBackupDirectory);

    /// <summary>Safety copies and automatic backups live in backups/auto next to the config (%LOCALAPPDATA%\DazeControl by default, D-103).</summary>
    private static BackupCardViewModel CreateBackupCard(ConfigSession session, ConfigStore store, EditorSettingsStore settings, Action<Action> post)
    {
        var auto = Program.ConfigPath is { } scratch
            ? Path.Combine(Path.GetDirectoryName(scratch)!, "backups", "auto")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Core.AppInfo.FolderName, "backups", "auto");
        var files = new PhysicalBackupFiles();
        var service = new BackupService(files, TimeProvider.System, ShellViewModel.Version, Environment.MachineName);
        var automatic = new AutoBackup(service, files, TimeProvider.System, store.Path, auto, Log);
        return new BackupCardViewModel(session, service, files, new RestoreTarget(store.Path, settings.Path, auto),
            store.BackupDirectory, TimeProvider.System, settings.Flush, auto: automatic, post: post);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        void Post(Action action) => ui.TryEnqueue(() => action());

        _engine = new EngineClient();
        var store = OpenStore();
        var session = new ConfigSession(store, TimeProvider.System, Post);
        session.Load();
        // With --config (a scratch file), view state goes next to it too, so test runs leave this PC's alone.
        var settings = new EditorSettingsStore(Program.ConfigPath is { } config
            ? Path.Combine(Path.GetDirectoryName(config)!, "editor.json")
            : EditorSettingsStore.DefaultPath, log: Log);
        // The channel file is per user like the auto-backup switch; with --config it sits next to the scratch file (D-108).
        var channelStore = new Core.Updates.UpdateChannelStore(Program.ConfigPath is { } scratch
            ? Path.Combine(Path.GetDirectoryName(scratch)!, "update-channel.txt")
            : Core.Updates.UpdateChannelStore.DefaultPath);
        var shell = new ShellViewModel(session, _engine, Post, LogsFolder, CreateUpdateClient(channelStore.Load()), settings,
            CreateBackupCard(session, store, settings, Post), channelStore);
        _window = new ShellWindow(shell);
        _window.Closed += (_, _) =>
        {
            shell.Dispose();
            session.Dispose(); // writes pending edits
            try { shell.Backup?.PendingAutoBackup?.Wait(TimeSpan.FromSeconds(3)); } // an automatic backup the last save started
            catch (AggregateException) { }
            settings.Dispose(); // and the view state
            _ = _engine.DisposeAsync().AsTask();
        };
        _engine.Start();
        _window.Activate();
    }
}
