using System.Diagnostics;
using DazeControl.Core;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Engine.Diagnostics;
using DazeControl.Engine.Input;
using DazeControl.Engine.Shell;
using DazeControl.Ipc;
using DazeControl.Runtime;
using DazeControl.Runtime.Control;
using DazeControl.Runtime.Lifecycle;
using DazeControl.Runtime.Menus;
using DazeControl.Runtime.Popup;
using DazeControl.Providers;
using DazeControl.Providers.Basic;
using DazeControl.Runtime.Providers;
using DazeControl.Runtime.Tray;
using DazeControl.Engine.Triggers;
using DazeControl.Runtime.Triggers;
using DazeControl.Core.Input;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace DazeControl.Engine;

/// <summary>Where the engine keeps its files.</summary>
/// <param name="ConfigArgument">The config given on the command line; null for the default one.</param>
internal sealed record EnginePaths(string Config, string History, string Logs, string? ConfigArgument = null)
{
    public static string DefaultConfig =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.FolderName, "config.yaml");

    public static EnginePaths Default(string? config = null)
    {
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.FolderName);
        return new(config ?? DefaultConfig, Path.Combine(local, "history.jsonl"), Log.DefaultDirectory, config);
    }
}

/// <summary>
/// The hand-written composition root (ARCHITECTURE → Overview): ConfigHost → FlowLauncher → FlowRunner, with the
/// tray and hotkeys on the message thread. Everything that reaches the shell from another thread (config reloads,
/// run events) is posted to the message thread first.
/// </summary>
internal sealed class EngineHost : IDisposable
{
    /// <summary>How long exit waits for running flows to wind down and their waiting clients to get answers.</summary>
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);

    private readonly MessageWindow _window;
    private readonly TrayIcon _tray;
    private readonly TrayNotifier _notices;
    private readonly GlobalHotkey _hotkeys;
    private readonly RunPopup _popup = new();
    private readonly FlowMenu _menu;
    private readonly RunPopupModel _popupModel = new();
    private readonly HotkeyTable _hotkeyTable;
    private readonly GamepadService _gamepads = new(new GamepadSource());
    private readonly ConfigHost _config;
    private readonly RunHistory _history;
    private readonly FlowLauncher _launcher;
    private readonly TriggerService _triggers;
    private readonly EngineController _controller;
    private readonly ScheduleSource _schedules;
    private readonly ResumeMonitor _resume;
    private readonly DesktopShortcuts _shortcuts;
    private readonly PipeServer _pipe;
    private readonly string _editorPath = Path.Combine(AppContext.BaseDirectory, "DazeControl.Editor.exe");

    // Message thread only.
    private readonly List<(Guid RunId, string FlowId)> _running = [];
    private string? _lastProblem;

    public EngineHost(EnginePaths paths, Func<INotifier, IEnumerable<IStepProvider>> providers, IDeviceQueries? devices = null,
        IReadOnlyList<IFactProbe>? probes = null)
    {
        _window = new MessageWindow();
        _tray = new TrayIcon(_window, AppInfo.ProductName, BuildMenu);
        _tray.FlowChosen += flowId => _controller!.TriggerAsync(flowId, "Tray");
        _tray.CommandChosen += OnTrayCommand;
        _tray.Selected = gesture =>
        {
            if (!TrayMenu.OpensEditor(gesture, File.Exists(_editorPath)))
                return false;
            OpenEditor();
            return true;
        };
        _hotkeys = new GlobalHotkey(_window);
        _menu = new FlowMenu(_window, new GamepadMenuFeed(_gamepads), TimeProvider.System);
        _hotkeyTable = new HotkeyTable(_hotkeys);
        _hotkeys.Pressed += OnHotkey;
        _hotkeyTable.FailuresChanged += OnShortcutFailuresChanged;
        // The run page arrives with M9; until then selecting a failure opens the editor.
        _popup.Selected += view => OpenEditor();

        _history = new RunHistory(paths.History);
        if (_history.LoadError is { } loadError)
            Log.Error($"history: {paths.History} not loaded", loadError);
        _history.WriteFailed += e => Log.Error("history: write failed", e);
        var stepProviders = providers(_notices = new TrayNotifier(_window, _tray)).ToList();
        var locks = new ResourceLocks();
        // flow.run (D-062): nested runs resolve flows in the current config; start mode goes through the launcher.
        var calls = new FlowCalls(() => _config!.Current);
        stepProviders.Add(new FlowProvider(calls));
        // menu.show (M12, D-055): one menu at a time; none over a full-screen game (#32); the window answers on the message thread.
        var menus = new GatedMenuPresenter(new FullScreenGuardPresenter(_menu, FullScreen.OverGame, Log.Info));
        stepProviders.Add(new MenuProvider(menus, calls));
        stepProviders.Add(new StopProvider()); // flow.stop (T24.1, D-133)
        // prompt.ask and ask-at-start inputs (M20, D-107) use the same window, so they never take focus either.
        var flagStore = stepProviders.OfType<DazeControl.Providers.Basic.FlagSetProvider>().FirstOrDefault()?.Flags;
        stepProviders.Add(new PromptProvider(menus, flagStore is null ? null : flagStore.Set));
        stepProviders.Add(new PickProvider(menus));
        var runner = new FlowRunner(stepProviders, TimeProvider.System, locks, events: OnRunEvent, probes: probes) { Flows = calls };
        _launcher = new FlowLauncher(runner, _history);
        calls.Launcher = _launcher;
        _launcher.StartPrompts = new InputPrompts(menus);
        // Event triggers (M20, D-107): sources load only while a flow subscribes; events are dropped while shortcuts are paused.
        var resume = _resume = new ResumeMonitor(_window);
        _triggers = new TriggerService([new SessionSource(_window), new PowerSource(_window, resume), new ScreenSource(_window), new DeviceSource(), new AudioSource(), new DisplaySource(_window), new NetworkSource(), new FlowEventSource(_launcher)], _launcher, calls, TimeProvider.System,
            () => _hotkeyTable.Paused, PowerCondition.All(), Log.Info);
        _launcher.RunCompleted += OnRunCompleted;
        _launcher.ListenerFailed += e => Log.Error("run: completion listener", e);

        _config = new ConfigHost(paths.Config, TimeProvider.System);
        _config.ListenerFailed += e => Log.Error("config: listener", e);
        _config.Changed += snapshot => _window.Post(() => Apply(snapshot));

        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DazeControl.exe");
        _shortcuts = FlowShortcuts.Create(paths.ConfigArgument, EnginePaths.DefaultConfig, Log.Info);
        var version = typeof(EngineHost).Assembly.GetName().Version?.ToString(3) ?? "?";
        _controller = new EngineController(() => _config.Current, _launcher, _history,
            EngineController.CreateDryRunner(TimeProvider.System), new Autostart(),
            new EngineInfo(version, Environment.ProcessId, exe, paths.Config, paths.ConfigArgument), Log.Info, devices,
            // Step tests share the providers and locks but raise no run events: no pop-up, no history.
            new FlowRunner(stepProviders, TimeProvider.System, locks, probes: probes) { Flows = calls },
            _shortcuts, _gamepads);
        _controller.RunRefused += refusal => _window.Post(() => ShowRefusal(refusal));
        _controller.HotkeyRecordingChanged += on => _window.Post(() => OnHotkeyRecording(on));
        // Schedules (M20, D-107): one due-time timer; a power resume or a clock change re-arms it and runs what came due.
        _schedules = new ScheduleSource(TimeProvider.System, (flowId, text) => _ = _controller.TriggerAsync(flowId, text), () => _hotkeyTable.Paused, Log.Info);
        resume.Resumed += _schedules.Rearm; // the same wake-up signal as power.resume (Modern Standby included)
        _window.Add((message, _, _) =>
        {
            if (message == 0x1E) // WM_TIMECHANGE
                _schedules.Rearm();
            return false;
        });
        _pipe = new PipeServer(Protocol.CurrentPipeName(), _controller.HandleAsync, (what, e) => Log.Error(what, e));
        _pipe.Start();
        Apply(_config.Current);
    }

    /// <summary>Runs the message loop until Exit (tray or WM_CLOSE), then winds the runs down.</summary>
    /// <param name="runFlowId">A desktop shortcut's flow (<c>--run</c>, D-049): it runs now that the config is loaded.</param>
    /// <param name="runInputs">The flow's inputs given after <c>--run</c> (T24.9).</param>
    public void Run(string? runFlowId = null, IReadOnlyDictionary<string, string>? runInputs = null)
    {
        Log.Info("engine: ready");
        _ = RepairShortcutIconsAsync();
        if (runFlowId is not null)
            _ = RunFromDesktopAsync(runFlowId, runInputs);
        MessageWindow.RunMessageLoop();
        Log.Info("engine: exiting");
        _hotkeyTable.Clear();
        _schedules.Dispose();
        _config.Dispose();
        // No new requests, cancel the runs, let every waiting client get its answer, then close the pipe; all of it
        // within the grace (drain gives up a second early so the pipe still closes cleanly).
        var exit = Task.Run(async () =>
        {
            using var drainGrace = new CancellationTokenSource(ExitGrace - TimeSpan.FromSeconds(1));
            await _pipe.StopAcceptingAsync().ConfigureAwait(false);
            try
            {
                await _launcher.CancelAllAsync().WaitAsync(drainGrace.Token).ConfigureAwait(false);
                await _pipe.DrainAsync(drainGrace.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Log.Warn("engine: runs or answers still pending at exit; closing the pipe");
            }
            await _pipe.DisposeAsync().ConfigureAwait(false);
        });
        if (!exit.Wait(ExitGrace))
            Log.Warn($"engine: runs still winding down after {ExitGrace.TotalSeconds:0} s; exiting anyway");
    }

    /// <summary>On the message thread. The snapshot may be older than <see cref="ConfigHost.Current"/>; the latest wins.</summary>
    private void Apply(ConfigSnapshot snapshot)
    {
        if (!ReferenceEquals(snapshot, _config.Current))
            return; // a newer reload is queued behind this one
        foreach (var problem in snapshot.Problems)
            Log.Info($"config: {problem}");
        _hotkeyTable.Apply(snapshot.RunnableFlows);
        _triggers.Apply(snapshot.RunnableFlows);
        _schedules.Apply(snapshot.RunnableFlows);
        // The wake-up signal is registered only while something needs it: schedules here, power.resume in PowerSource.
        try
        {
            if (_schedules.Count > 0)
                _resume.Retain(_schedules);
            else
                _resume.Release(_schedules);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error("ResumeMonitor retain/release failed (only classic resume works)", e);
        }
        foreach (var invalid in _hotkeyTable.Invalid)
            Log.Info($"hotkey: not a valid shortcut: {invalid}");
        Log.Info($"config: {snapshot.Flows.Count} flow(s), {snapshot.RunnableFlows.Count()} runnable, "
            + $"{_hotkeyTable.Wanted.Count - _hotkeyTable.Failures.Count} shortcut(s)");
        PublishShell();
        UpdateTray();
        _pipe.Broadcast(new PipeEvent { Kind = EventKinds.ConfigReloaded, At = DateTimeOffset.UtcNow });
    }

    private void PublishShell() => _controller.Shell = new ShellStatus(_hotkeyTable.Paused,
        [.. _hotkeyTable.Failures.Select(f => $"{f.Key}: {f.Value}")]);

    /// <summary>The menu opens: a chord another app has let go of since is registered now (D-072).</summary>
    private IReadOnlyList<TrayEntry> BuildMenu()
    {
        _hotkeyTable.RetryFailures();
        return TrayMenu.Build(_config.Current, new TrayState(
            _running.Select(r => r.FlowId).ToHashSet(), id => _hotkeyTable.ShortcutOf(id)?.ToString(),
            _hotkeyTable.Paused, File.Exists(_editorPath)));
    }

    /// <summary>On the message thread. Messages are posted in order, so the last one wins.</summary>
    private void OnHotkeyRecording(bool on)
    {
        if (on == _hotkeyTable.Recording)
            return;
        if (on)
            _hotkeyTable.StartRecording();
        else
            _hotkeyTable.StopRecording();
        Log.Info($"hotkey: shortcuts {(on ? "let go while the editor records one" : "back after recording")}");
    }

    /// <summary>On the message thread: a retry or a reload changed which chords failed; the editor reads status again.</summary>
    private void OnShortcutFailuresChanged()
    {
        foreach (var (hotkey, why) in _hotkeyTable.Failures)
            Log.Info($"hotkey: {hotkey} for {_hotkeyTable.Wanted[hotkey]}: {why}");
        PublishShell();
        _pipe?.Broadcast(new PipeEvent { Kind = EventKinds.ShortcutsChanged, At = DateTimeOffset.UtcNow });
    }

    private void OnTrayCommand(TrayCommand command)
    {
        switch (command)
        {
            case TrayCommand.Exit:
                _window.Close();
                break;
            case TrayCommand.PauseShortcuts:
                if (_hotkeyTable.Paused)
                    _hotkeyTable.Resume();
                else
                    _hotkeyTable.Pause();
                Log.Info($"hotkey: shortcuts {(_hotkeyTable.Paused ? "paused" : "resumed")}");
                PublishShell();
                UpdateTray();
                break;
            case TrayCommand.OpenEditor:
                OpenEditor();
                break;
        }
    }

    /// <summary>The editor brings its own window to the front (single instance, T6.1).</summary>
    private void OpenEditor()
    {
        // The tray click made us the foreground's owner; pass that on, so the running editor can come to the front.
        PInvoke.AllowSetForegroundWindow(unchecked((uint)-1)); // ASFW_ANY
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(_editorPath) { UseShellExecute = false });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Log.Error($"editor: {_editorPath} didn't start", e);
        }
    }

    private string NameOf(string flowId) =>
        _config.Current.Flows.FirstOrDefault(f => f.FlowId == flowId) is { } state
            ? (state.Running ?? state.Written)?.Name ?? flowId
            : flowId;

    private void UpdateTray()
    {
        var kind = _running.Count > 0 ? TrayGlyphKind.Busy : _hotkeyTable.Paused ? TrayGlyphKind.Paused : TrayGlyphKind.Idle;
        _tray.Update(kind, TrayMenu.Tooltip([.. _running.Select(r => NameOf(r.FlowId)).Distinct()], _hotkeyTable.Paused, _lastProblem));
    }

    /// <summary>On the message thread.</summary>
    private void OnRunEventPosted(RunEvent e)
    {
        switch (e)
        {
            case RunStarted started:
                _running.Add((started.RunId, started.FlowId));
                break;
            case RunFinished finished:
                _running.RemoveAll(r => r.RunId == finished.RunId);
                if (finished.Outcome != FlowOutcome.Cancelled)
                    _lastProblem = TrayMenu.Problem(NameOf(finished.FlowId), finished.Outcome);
                break;
            default:
                return;
        }
        UpdateTray();
        ShowPopup(e);
    }

    /// <summary>On the message thread, in run-event order. A pop-up failure never touches the run (fail-soft).</summary>
    private void ShowPopup(RunEvent e)
    {
        try
        {
            // The run's own snapshot: a reload since it was queued must not rename or silence its pop-up.
            var settings = _config.Current.Settings.Popup;
            if (_popupModel.Apply(e, null, settings))
            {
                // A quiet finish (a menu closed with nothing picked) leaves no view: take down what the run showed.
                // Any window of ours knocks a DXGI full-screen game out of full screen (X-010): history keeps the run.
                if (_popupModel.Current is { } view)
                {
                    if (!FullScreen.OverGame())
                        _popup.Show(view, settings.Position);
                }
                else
                    _popup.Hide();
            }
        }
        catch (Exception failure)
        {
            Log.Error("popup: not shown", failure);
        }
    }

    /// <summary>
    /// Once per start, off the message thread: an update re-points our desktop shortcuts at the main exe's icon (D-106),
    /// so each gets its flow's icon back. A failure only leaves the plain icon.
    /// </summary>
    private async Task RepairShortcutIconsAsync()
    {
        try
        {
            var flows = _config.Current.Flows.Select(f => f.Running ?? f.Written).OfType<Flow>().ToList();
            var repaired = await Task.Run(() => _shortcuts.RepairIcons(flows)).ConfigureAwait(false);
            if (repaired > 0)
                Log.Info($"shortcuts: {repaired} icon(s) put back");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error("shortcuts: icon repair", e);
        }
    }

    /// <summary>A shortcut that started this engine: the same request a second engine forwards over the pipe.</summary>
    private async Task RunFromDesktopAsync(string flowId, IReadOnlyDictionary<string, string>? inputs)
    {
        try
        {
            await _controller.HandleAsync(DesktopRun(flowId, inputs: inputs), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error($"run: {flowId} from a desktop shortcut", e);
        }
    }

    /// <summary>Id only, never a name (D-049); no wait: the pop-up and history tell the rest.</summary>
    /// <param name="configPath">The shortcut's config (full path), when forwarded to an engine that may run another.</param>
    /// <param name="inputs">Given after <c>--run</c> (T24.9); checked by the controller, a bad one is the pop-up.</param>
    public static PipeRequest DesktopRun(string flowId, string? configPath = null, IReadOnlyDictionary<string, string>? inputs = null) =>
        new()
        {
            Op = Ops.Run, Flow = flowId, ExactId = true, Trigger = EngineController.DesktopTrigger, ConfigPath = configPath,
            Inputs = inputs is { Count: > 0 } ? new Dictionary<string, string>(inputs, StringComparer.Ordinal) : null,
        };

    /// <summary>On the message thread.</summary>
    private void ShowRefusal(RunRefusal refusal)
    {
        try
        {
            if (FullScreen.OverGame())
                return;
            var settings = _config.Current.Settings.Popup;
            _popup.Show(_popupModel.Refused(refusal.FlowId, refusal.Title, refusal.Message, settings), settings.Position);
        }
        catch (Exception failure)
        {
            Log.Error("popup: not shown", failure);
        }
    }

    private void OnHotkey(int id)
    {
        if (_hotkeyTable.Resolve(id) is not var (flowId, hotkey))
            return;
        _controller.TriggerAsync(flowId, hotkey.ToString());
    }

    /// <summary>
    /// From pool threads (FlowRunner): log, then hand the rest to the message thread. Quick. The runner's
    /// RunFinished is skipped: <see cref="OnRunCompleted"/> publishes it once history and the launcher caught up.
    /// </summary>
    private void OnRunEvent(RunEvent e)
    {
        if (e.ParentRunId is not null)
        {
            // A nested run (D-062): only the top run's pop-up, tray bullet and Stop; the pop-up folds it in (#59).
            if (e is NodeFinished { Result: { Outcome: not NodeOutcome.Succeeded } nested })
                Log.Info($"run: {e.FlowId}/{nested.NodeId} {nested.Outcome} (inside a call){(nested.Error is null ? "" : ": " + nested.Error)}");
            _window.Post(() => ShowPopup(e));
            _pipe?.Broadcast(PipeEvent.From(e));
            return;
        }
        switch (e)
        {
            case RunFinished:
                return;
            case RunStarted:
                _window.Post(() => OnRunEventPosted(e));
                break;
            case NodeStarted:
                _window.Post(() => ShowPopup(e));
                break;
            case NodeFinished { Result: { Outcome: not NodeOutcome.Succeeded } result } node:
                Log.Info($"run: {node.FlowId}/{result.NodeId} {result.Outcome}{(result.Error is null ? "" : ": " + result.Error)}");
                _window.Post(() => ShowPopup(e));
                break;
            case NodeFinished:
                _window.Post(() => ShowPopup(e));
                break;
        }
        _pipe?.Broadcast(PipeEvent.From(e));
    }

    /// <summary>From a pool thread (FlowLauncher), after the run is in the history and its slot is free.</summary>
    private void OnRunCompleted(RunFinished finished)
    {
        Log.Info($"run: {finished.FlowId} {finished.Outcome}");
        _window.Post(() => OnRunEventPosted(finished));
        _pipe?.Broadcast(PipeEvent.From(finished));
    }

    public void Dispose()
    {
        _triggers.Dispose();
        _resume.Dispose();
        _gamepads.Dispose();
        _hotkeys.Dispose();
        _menu.Dispose();
        _popup.Dispose();
        _notices.Dispose();
        _tray.Dispose();
        _window.Dispose();
    }
}
