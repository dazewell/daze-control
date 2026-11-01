using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using DazeControl.Core;
using DazeControl.Editor.ViewModels.Config;
using DazeControl.Editor.ViewModels.Engine;

namespace DazeControl.Editor.ViewModels;

public enum EditorPage
{
    Flows,
    Library,
    Settings,
    About,
}

/// <summary>Editor window state (UX §5.1): the page shown, the engine's presence, the pages' models.</summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IEngineClient _engine;
    private readonly Action<Action> _post;

    public ShellViewModel(ConfigSession session, IEngineClient engine, Action<Action> post, string logsFolder)
    {
        Session = session;
        _engine = engine;
        _post = post;
        Flows = new FlowsPageViewModel(session, engine, post);
        Settings = new SettingsPageViewModel(session, engine, post, logsFolder);
        _engine.StateChanged += OnEngineStateChanged;
        EngineRunning = engine.IsRunning;
    }

    public ConfigSession Session { get; }

    public FlowsPageViewModel Flows { get; }

    public SettingsPageViewModel Settings { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    public partial EditorPage Page { get; set; }

    public string Subtitle => Page.ToString();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EngineMissing))]
    public partial bool EngineRunning { get; private set; }

    public bool EngineMissing => !EngineRunning;

    public static string Version { get; } =
        typeof(ShellViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";

    /// <summary>About › Copy details.</summary>
    public string Details(string windowsAppSdk) => string.Join(Environment.NewLine,
        $"{AppInfo.ProductName} {Version}",
        $"Windows {Environment.OSVersion.Version} {RuntimeInformation.OSArchitecture}",
        $"{RuntimeInformation.FrameworkDescription}, Windows App SDK {windowsAppSdk}",
        $"Engine: {(EngineRunning ? "running" : "not running")}",
        $"Config: {Session.Path}");

    private void OnEngineStateChanged() => _post(() => EngineRunning = _engine.IsRunning);

    public void Dispose()
    {
        _engine.StateChanged -= OnEngineStateChanged;
        Flows.Dispose();
        Settings.Dispose();
    }
}
