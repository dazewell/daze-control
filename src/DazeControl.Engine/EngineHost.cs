using System.Diagnostics;
using DazeControl.Core;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Engine.Diagnostics;
using DazeControl.Engine.Shell;
using DazeControl.Ipc;
using DazeControl.Runtime;
using DazeControl.Runtime.Control;
using DazeControl.Runtime.Lifecycle;
using DazeControl.Runtime.Popup;
using DazeControl.Providers.Basic;
using DazeControl.Runtime.Providers;
using DazeControl.Runtime.Tray;
using DazeControl.Runtime.Triggers;
using Windows.Win32;

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
    private readonly GlobalHotkey _hotkeys;
    private readonly RunPopup _popup = new();
    private readonly RunPopupModel _popupModel = new();
    private readonly HotkeyTable _hotkeyTable;
    private readonly ConfigHost _config;
    private readonly RunHistory _history;
    private readonly FlowLauncher _launcher;
    private readonly EngineController _controller;
    private readonly PipeServer _pipe;
    private readonly string _editorPath = Path.Combine(AppContext.BaseDirectory, "DazeControl.Editor.exe");

    // Message thread only.
    private readonly List<(Guid RunId, string FlowId)> _running = [];
    private string? _lastProblem;

    public EngineHost(EnginePaths paths, Func<INotifier, IEnumerable<IStepProvider>> providers, IDeviceQueries? devices = null)
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
        _hotkeyTable = new HotkeyTable(_hotkeys);
        _hotkeys.Pressed += OnHotkey;
        // The run page arrives with M9; until then selecting a failure opens the editor.
        _popup.Selected += view => OpenEditor();

        _history = new RunHistory(paths.History);
        if (_history.LoadError is { } loadError)
            Log.Error($"history: {paths.History} not loaded", loadError);
        _history.WriteFailed += e => Log.Error("history: write failed", e);
        var stepProviders = providers(new TrayNotifier(_window, _tray)).ToList();
        var locks = new ResourceLocks();
        var runner = new FlowRunner(stepProviders, TimeProvider.System, locks, events: OnRunEvent);
        _launcher = new FlowLauncher(runner, _history);
        _launcher.RunCompleted += OnRunCompleted;
        _launcher.ListenerFailed += e => Log.Error("run: completion listener", e);

        _config = new ConfigHost(paths.Config, TimeProvider.System);
        _config.ListenerFailed += e => Log.Error("config: listener", e);
        _config.Changed += snapshot => _window.Post(() => Apply(snapshot));

        var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "DazeControl.exe");
        var version = typeof(EngineHost).Assembly.GetName().Version?.ToString(3) ?? "?";
        _controller = new EngineController(() => _config.Current, _launcher, _history,
            EngineController.CreateDryRunner(TimeProvider.System), new Autostart(),
            new EngineInfo(version, Environment.ProcessId, exe, paths.Config, paths.ConfigArgument), Log.Info, devices,
            // Step tests share the providers and locks but raise no run events: no pop-up, no history.
            new FlowRunner(stepProviders, TimeProvider.System, locks),
            FlowShortcuts.Create(paths.ConfigArgument, EnginePaths.DefaultConfig, Log.Info));
        _controller.RunRefused += refusal => _window.Post(() => ShowRefusal(refusal));
        _pipe = new PipeServer(Protocol.CurrentPipeName(), _controller.HandleAsync, (what, e) => Log.Error(what, e));
        _pipe.Start();
        Apply(_config.Current);
    }

    /// <summary>Runs the message loop until Exit (tray or WM_CLOSE), then winds the runs down.</summary>
    /// <param name="runFlowId">A desktop shortcut's flow (<c>--run</c>, D-049): it runs now that the config is loaded.</param>
    public void Run(string? runFlowId = null)
    {
        Log.Info("engine: ready");
        if (runFlowId is not null)
            _ = RunFromDesktopAsync(runFlowId);
        MessageWindow.RunMessageLoop();
        Log.Info("engine: exiting");
        _hotkeyTable.Clear();
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
        foreach (var invalid in _hotkeyTable.Invalid)
            Log.Info($"hotkey: not a valid shortcut: {invalid}");
        foreach (var (hotkey, why) in _hotkeyTable.Failures)
            Log.Info($"hotkey: {hotkey} for {_hotkeyTable.Wanted[hotkey]}: {why}");
        Log.Info($"config: {snapshot.Flows.Count} flow(s), {snapshot.RunnableFlows.Count()} runnable, "
            + $"{_hotkeyTable.Wanted.Count - _hotkeyTable.Failures.Count} shortcut(s)");
        PublishShell();
        UpdateTray();
        _pipe.Broadcast(new PipeEvent { Kind = EventKinds.ConfigReloaded, At = DateTimeOffset.UtcNow });
    }

    private void PublishShell() => _controller.Shell = new ShellStatus(_hotkeyTable.Paused,
        [.. _hotkeyTable.Failures.Select(f => $"{f.Key}: {f.Value}")]);

    private IReadOnlyList<TrayEntry> BuildMenu() => TrayMenu.Build(_config.Current, new TrayState(
        _running.Select(r => r.FlowId).ToHashSet(), id => _hotkeyTable.ShortcutOf(id)?.ToString(),
        _hotkeyTable.Paused, File.Exists(_editorPath)));

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
            if (_popupModel.Apply(e, null, settings) && _popupModel.Current is { } view)
                _popup.Show(view, settings.Position);
        }
        catch (Exception failure)
        {
            Log.Error("popup: not shown", failure);
        }
    }

    /// <summary>A shortcut that started this engine: the same request a second engine forwards over the pipe.</summary>
    private async Task RunFromDesktopAsync(string flowId)
    {
        try
        {
            await _controller.HandleAsync(DesktopRun(flowId), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log.Error($"run: {flowId} from a desktop shortcut", e);
        }
    }

    /// <summary>Id only, never a name (D-049); no wait: the pop-up and history tell the rest.</summary>
    public static PipeRequest DesktopRun(string flowId) =>
        new() { Op = Ops.Run, Flow = flowId, ExactId = true, Trigger = EngineController.DesktopTrigger };

    /// <summary>On the message thread.</summary>
    private void ShowRefusal(RunRefusal refusal)
    {
        try
        {
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
        _hotkeys.Dispose();
        _popup.Dispose();
        _tray.Dispose();
        _window.Dispose();
    }
}
