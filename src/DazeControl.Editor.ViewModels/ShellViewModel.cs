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
        Core.Updates.IUpdateClient? updates = null, EditorSettingsStore? editorSettings = null)
    {
        Session = session;
        EditorSettings = editorSettings;
        IsNavPaneOpen = editorSettings?.Settings.NavPaneOpen ?? true;
        _engine = engine;
        _post = post;
        Desktop = new Engine.DesktopShortcutStore(engine, session.Path);
        Flows = new FlowsPageViewModel(session, engine, post, Desktop);
        Settings = new SettingsPageViewModel(session, engine, post, logsFolder);
        Updates = new UpdatesViewModel(updates, post);
        Library = new LibraryPage.LibraryPageViewModel(session, engine, post);
        Runs = new Runs.RunsPageViewModel(session, engine, post);
        Library.FlowRequested += OnLibraryFlowRequested;
        Session.FlowRenamed += OnFlowRenamed;
        _engine.StateChanged += OnEngineStateChanged;
        EngineRunning = engine.IsRunning;
    }

    public ConfigSession Session { get; }

    /// <summary>This PC's view state (editor.json, D-068); null in tests that don't need it.</summary>
    public EditorSettingsStore? EditorSettings { get; }

    /// <summary>The nav pane at full width, else the icon strip (P9); remembered in editor.json.</summary>
    [ObservableProperty]
    public partial bool IsNavPaneOpen { get; set; }

    partial void OnIsNavPaneOpenChanged(bool value)
    {
        if (EditorSettings is { } store && store.Settings.NavPaneOpen != value)
            store.Update(s => s.NavPaneOpen = value);
    }

    /// <summary>Desktop shortcuts, shared by the Flows list and the open flow's settings.</summary>
    public Engine.DesktopShortcutStore Desktop { get; }

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

    /// <summary>A saved rename (D-075): the open flow follows by itself, its view choices at once, its desktop shortcut once the engine has it.</summary>
    private void OnFlowRenamed(string oldId, string newId)
    {
        EditorSettings?.RenameFlow(oldId, newId);
        _ = Desktop.MoveAsync(oldId, newId);
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
        _ = Flows.EndUndoAsync(); // opening a flow ends the chance to undo a Delete (D-076)
        CloseFlow();
        if (!Session.Current.Flows.Any(f => f.Id == flowId))
            return;
        var editor = new Canvas.FlowCanvasViewModel(Session, flowId, _clipboard, EditorSettings);
        Inspector = new Inspector.InspectorViewModel(Session, editor, _engine, _post, desktop: Desktop);
        Inspector.OpenFlowRequested += OnCalledFlowRequested;
        Inspector.AndroidInputsRequested += OnAndroidInputsRequested;
        Run = new Runs.RunOverlayViewModel(editor, _engine, _post);
        Editor = editor;
    }

    /// <summary>"Run a flow" › Open this flow: the window asks about the draft (D-047), then calls <see cref="OpenFlow"/>.</summary>
    public event Action<string>? CalledFlowRequested;

    private void OnCalledFlowRequested(string flowId) => CalledFlowRequested?.Invoke(flowId);

    /// <summary>
    /// Android input › "Add inputs in the Library" (L1): the Library's Android section has the device's inputs open;
    /// the window asks about the draft (D-047), then shows the Library page.
    /// </summary>
    public event Action? LibraryRequested;

    private void OnAndroidInputsRequested(string deviceKey)
    {
        Library.RevealInputs(deviceKey);
        LibraryRequested?.Invoke();
    }

    public void CloseFlow()
    {
        Run?.Dispose();
        Run = null;
        if (Inspector is not null)
        {
            Inspector.OpenFlowRequested -= OnCalledFlowRequested;
            Inspector.AndroidInputsRequested -= OnAndroidInputsRequested;
        }
        Inspector?.Dispose();
        Inspector = null;
        Editor?.Dispose();
        Editor = null;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    public partial EditorPage Page { get; set; }

    public string Subtitle => Page.ToString();

    partial void OnPageChanged(EditorPage value)
    {
        if (value == EditorPage.Flows)
            Flows.RefreshTimes();
    }

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
        Session.FlowRenamed -= OnFlowRenamed;
        Run?.Dispose();
        Inspector?.Dispose();
        Editor?.Dispose();
        Flows.Dispose();
        Settings.Dispose();
        Library.Dispose();
        Runs.Dispose();
    }
}
