using System.Reflection;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CtrlAltDaze.Core;
using CtrlAltDaze.Editor.ViewModels.Config;
using CtrlAltDaze.Editor.ViewModels.Engine;

namespace CtrlAltDaze.Editor.ViewModels;

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
        Core.Updates.IUpdateClient? updates = null, EditorSettingsStore? editorSettings = null,
        BackupCard.BackupCardViewModel? backup = null, Core.Updates.UpdateChannelStore? updateChannel = null)
    {
        Backup = backup;
        Session = session;
        EditorSettings = editorSettings;
        IsNavPaneOpen = editorSettings?.Settings.NavPaneOpen ?? true;
        _engine = engine;
        _post = post;
        Desktop = new Engine.DesktopShortcutStore(engine, session.Path);
        Flows = new FlowsPageViewModel(session, engine, post, Desktop, settings: editorSettings);
        Settings = new SettingsPageViewModel(session, engine, post, logsFolder);
        Updates = new UpdatesViewModel(updates, post, updateChannel);
        Library = new LibraryPage.LibraryPageViewModel(session, engine, post);
        Runs = new Runs.RunsPageViewModel(session, engine, post);
        Library.FlowRequested += OnLibraryFlowRequested;
        Flows.FlowCreated += OpenFlow;
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

    /// <summary>Settings › Backup and restore; null in tests that don't need it.</summary>
    public BackupCard.BackupCardViewModel? Backup { get; }

    public LibraryPage.LibraryPageViewModel Library { get; }

    public Runs.RunsPageViewModel Runs { get; }

    /// <summary>Back/forward (D-095): fed by page and flow changes, not by the pages.</summary>
    public NavigationHistory History { get; } = new();

    private int _quiet;

    private EditorLocation Here => Editor is { } e ? new(EditorPage.Flows, e.FlowId) : new(Page);

    private void RecordVisit()
    {
        if (_quiet == 0)
            History.Visit(Here);
    }

    private bool Exists(EditorLocation l) => l.FlowId is null || Session.Current.Flows.Any(f => f.Id == l.FlowId);

    public bool CanGoBack => History.PeekBack(Exists) is not null;

    public bool CanGoForward => History.PeekForward(Exists) is not null;

    /// <summary>The place Back/Forward would show; the window asks about a draft first (D-047), then calls <see cref="Go"/>.</summary>
    public EditorLocation? Target(bool forward) => forward ? History.PeekForward(Exists) : History.PeekBack(Exists);

    /// <summary>Shows the place <see cref="Target"/> gave and moves the history there.</summary>
    public void Go(bool forward)
    {
        if (Target(forward) is not { } target)
            return;
        _quiet++;
        try
        {
            CloseFlow();
            if (target.FlowId is { } id)
            {
                OpenFlow(id);
                Page = EditorPage.Flows;
            }
            else
                Page = target.Page;
        }
        finally
        {
            _quiet--;
        }
        if (forward)
            History.CommitForward();
        else
            History.CommitBack();
    }

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
        History.Rename(oldId, newId);
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

    partial void OnEditorChanged(Canvas.FlowCanvasViewModel? value) => RecordVisit();

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
        _quiet++; // closing the old flow on the way is not a place of its own
        try
        {
            CloseFlow();
        }
        finally
        {
            _quiet--;
        }
        if (!Session.Current.Flows.Any(f => f.Id == flowId))
        {
            RecordVisit(); // the list, if a flow was open
            return;
        }
        var editor = new Canvas.FlowCanvasViewModel(Session, flowId, _clipboard, EditorSettings);
        Inspector = new Inspector.InspectorViewModel(Session, editor, _engine, _post, desktop: Desktop);
        Inspector.OpenFlowRequested += OnCalledFlowRequested;
        Inspector.AndroidInputsRequested += OnAndroidInputsRequested;
        Inspector.BoundDevicesRequested += OnBoundDevicesRequested;
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

    /// <summary>An input's "Bind devices in Library": same hand-over as the Android inputs, without a device to reveal.</summary>
    private void OnBoundDevicesRequested() => LibraryRequested?.Invoke();

    public void CloseFlow()
    {
        Run?.Dispose();
        Run = null;
        if (Inspector is not null)
        {
            Inspector.OpenFlowRequested -= OnCalledFlowRequested;
            Inspector.AndroidInputsRequested -= OnAndroidInputsRequested;
            Inspector.BoundDevicesRequested -= OnBoundDevicesRequested;
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
        RecordVisit();
        if (value == EditorPage.Flows)
            Flows.RefreshTimes();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EngineMissing))]
    public partial bool EngineRunning { get; private set; }

    public bool EngineMissing => !EngineRunning;

    public static string Version { get; } =
        StripBuildMetadata(typeof(ShellViewModel).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion)
        ?? "0.0.0";

    /// <summary>The installed version without Velopack's or MSBuild's <c>+hash</c>: <c>0.52.0-rc.1+abc</c> shows as <c>0.52.0-rc.1</c>.</summary>
    public static string? StripBuildMetadata(string? version) => version?.Split('+')[0];

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
        Flows.FlowCreated -= OpenFlow;
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
