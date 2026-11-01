using System.Diagnostics;
using DazeControl.Core;
using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Engine.Diagnostics;
using DazeControl.Engine.Shell;
using DazeControl.Runtime;
using DazeControl.Runtime.Providers;
using DazeControl.Runtime.Tray;
using DazeControl.Runtime.Triggers;

namespace DazeControl.Engine;

/// <summary>Where the engine keeps its files.</summary>
internal sealed record EnginePaths(string Config, string History, string Logs)
{
    public static EnginePaths Default(string? config = null)
    {
        var roaming = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), AppInfo.FolderName);
        var local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppInfo.FolderName);
        return new(config ?? Path.Combine(roaming, "config.yaml"), Path.Combine(local, "history.jsonl"), Log.DefaultDirectory);
    }
}

/// <summary>
/// The hand-written composition root (ARCHITECTURE → Overview): ConfigHost → FlowLauncher → FlowRunner, with the
/// tray and hotkeys on the message thread. Everything that reaches the shell from another thread (config reloads,
/// run events) is posted to the message thread first.
/// </summary>
internal sealed class EngineHost : IDisposable
{
    /// <summary>How long exit waits for running flows to wind down after cancelling them.</summary>
    private static readonly TimeSpan ExitGrace = TimeSpan.FromSeconds(5);

    private readonly MessageWindow _window;
    private readonly TrayIcon _tray;
    private readonly GlobalHotkey _hotkeys;
    private readonly HotkeyTable _hotkeyTable;
    private readonly ConfigHost _config;
    private readonly RunHistory _history;
    private readonly FlowLauncher _launcher;
    private readonly string _editorPath = Path.Combine(AppContext.BaseDirectory, "DazeControl.Editor.exe");

    // Message thread only.
    private readonly List<(Guid RunId, string FlowId)> _running = [];
    private string? _lastProblem;

    public EngineHost(EnginePaths paths, IEnumerable<IStepProvider> providers)
    {
        _window = new MessageWindow();
        _tray = new TrayIcon(_window, AppInfo.ProductName, BuildMenu);
        _tray.FlowChosen += flowId => Trigger(flowId, "Tray");
        _tray.CommandChosen += OnTrayCommand;
        _hotkeys = new GlobalHotkey(_window);
        _hotkeyTable = new HotkeyTable(_hotkeys);
        _hotkeys.Pressed += OnHotkey;

        _history = new RunHistory(paths.History);
        if (_history.LoadError is { } loadError)
            Log.Error($"history: {paths.History} not loaded", loadError);
        _history.WriteFailed += e => Log.Error("history: write failed", e);
        var runner = new FlowRunner(providers, TimeProvider.System, new ResourceLocks(), events: OnRunEvent);
        _launcher = new FlowLauncher(runner, _history);

        _config = new ConfigHost(paths.Config, TimeProvider.System);
        _config.ListenerFailed += e => Log.Error("config: listener", e);
        _config.Changed += snapshot => _window.Post(() => Apply(snapshot));
        Apply(_config.Current);
    }

    /// <summary>Runs the message loop until Exit (tray or WM_CLOSE), then winds the runs down.</summary>
    public void Run()
    {
        Log.Info("engine: ready");
        MessageWindow.RunMessageLoop();
        Log.Info("engine: exiting");
        _hotkeyTable.Clear();
        _config.Dispose();
        if (!_launcher.CancelAllAsync().Wait(ExitGrace))
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
        UpdateTray();
    }

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
    }

    private void OnHotkey(int id)
    {
        if (_hotkeyTable.Resolve(id) is not var (flowId, hotkey))
            return;
        Trigger(flowId, hotkey.ToString());
    }

    /// <summary>Starts a flow from a trigger; the run continues on the pool and is logged when it ends.</summary>
    public Task<RunResult?> Trigger(string flowId, string trigger)
    {
        var state = _config.Current.Flows.FirstOrDefault(f => f.FlowId == flowId);
        if (state?.Running is not { Enabled: true } flow)
        {
            Log.Info($"run: {flowId} ({trigger}) not runnable");
            return Task.FromResult<RunResult?>(null);
        }
        Log.Info($"run: {flowId} triggered by {trigger}");
        var run = _launcher.TriggerAsync(flow, state.Library, trigger);
        _ = run.ContinueWith(t =>
        {
            if (t.IsFaulted)
                Log.Error($"run: {flowId} failed in the runner", t.Exception!);
            else if (t.Result is null)
                Log.Info($"run: {flowId} ({trigger}) ignored, already running");
        }, TaskScheduler.Default);
        return run;
    }

    /// <summary>From pool threads (FlowRunner): log, then hand the rest to the message thread. Quick.</summary>
    private void OnRunEvent(RunEvent e)
    {
        if (e is RunStarted or RunFinished)
            _window.Post(() => OnRunEventPosted(e));
        switch (e)
        {
            case RunFinished finished:
                Log.Info($"run: {finished.FlowId} {finished.Outcome}");
                break;
            case NodeFinished { Result: { Outcome: not NodeOutcome.Succeeded } result } node:
                Log.Info($"run: {node.FlowId}/{result.NodeId} {result.Outcome}{(result.Error is null ? "" : ": " + result.Error)}");
                break;
        }
    }

    public void Dispose()
    {
        _hotkeys.Dispose();
        _tray.Dispose();
        _window.Dispose();
    }
}
