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
    Runs,
    Settings,
    About,
}

/// <summary>Editor window state (UX §5.1): the page shown, the engine's presence, the pages' models.</summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly IEngineClient _engine;
    private readonly Action<Action> _post;

    public ShellViewModel(ConfigSession session, IEngineClient engine, Action<Action> post, string logsFolder,
        Core.Updates.IUpdateClient? updates = null)
    {
        Session = session;
        _engine = engine;
        _post = post;
        Flows = new FlowsPageViewModel(session, engine, post);
        Settings = new SettingsPageViewModel(session, engine, post, logsFolder);
        Updates = new UpdatesViewModel(updates, post);
        Library = new LibraryPage.LibraryPageViewModel(session, engine, post);
        Runs = new Runs.RunsPageViewModel(session, engine, post);
        Library.FlowRequested += OnLibraryFlowRequested;
        _engine.StateChanged += OnEngineStateChanged;
        EngineRunning = engine.IsRunning;
    }

    public ConfigSession Session { get; }

    public FlowsPageViewModel Flows { get; }

    public SettingsPageViewModel Settings { get; }

    public UpdatesViewModel Updates { get; }

    public LibraryPage.LibraryPageViewModel Library { get; }

    public Runs.RunsPageViewModel Runs { get; }

    /// <summary>Library › "Used by" › a flow: that flow in the editor.</summary>
    private void OnLibraryFlowRequested(string flowId)
    {
        OpenFlow(flowId);
        if (Editor?.FlowId == flowId)
            Page = EditorPage.Flows;
    }

    /// <summary>"Open in editor" on the Runs page: the flow, with that run's states on the canvas.</summary>
    public void OpenRun(Core.Runs.RunRecord run)
    {
        ArgumentNullException.ThrowIfNull(run);
        OpenFlow(run.FlowId);
        if (Editor?.FlowId != run.FlowId)
            return;
        Page = EditorPage.Flows;
        Run?.Show(run);
    }

    /// <summary>The flow being edited (Flows › name); null on the Flows list.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingFlow))]
    public partial Canvas.FlowCanvasViewModel? Editor { get; private set; }

    public bool IsEditingFlow => Editor is not null;

    /// <summary>The flow editor's right pane; set before <see cref="Editor"/>, so it is there when the editor shows.</summary>
    public Inspector.InspectorViewModel? Inspector { get; private set; }

    /// <summary>The flow editor's runs (CommandBar, InfoBar, states on the canvas); set with <see cref="Inspector"/>.</summary>
    public Runs.RunOverlayViewModel? Run { get; private set; }

    private readonly Canvas.CanvasClipboard _clipboard = new();

    public void OpenFlow(string flowId)
    {
        if (Editor?.FlowId == flowId)
            return;
        CloseFlow();
        if (!Session.Current.Flows.Any(f => f.Id == flowId))
            return;
        var editor = new Canvas.FlowCanvasViewModel(Session, flowId, _clipboard);
        Inspector = new Inspector.InspectorViewModel(Session, editor, _engine, _post);
        Run = new Runs.RunOverlayViewModel(editor, _engine, _post);
        Editor = editor;
    }

    public void CloseFlow()
    {
        Run?.Dispose();
        Run = null;
        Inspector?.Dispose();
        Inspector = null;
        Editor?.Dispose();
        Editor = null;
    }

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
        Library.FlowRequested -= OnLibraryFlowRequested;
        Run?.Dispose();
        Inspector?.Dispose();
        Editor?.Dispose();
        Flows.Dispose();
        Settings.Dispose();
        Library.Dispose();
        Runs.Dispose();
    }
}
