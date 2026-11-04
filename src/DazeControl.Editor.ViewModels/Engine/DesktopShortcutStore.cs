using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Engine;

/// <summary>
/// The desktop shortcuts as the engine last reported them (D-049), shared by the Flows list and the flow settings so
/// a change in one shows in the other. The engine writes them; without it there's nothing to show (UX §5.7 #25).
/// </summary>
/// <param name="configPath">The file the editor has open; the engine refuses when it runs another one.</param>
public sealed class DesktopShortcutStore(IEngineClient engine, string? configPath = null)
{
    /// <summary>UX §5.7 #23.</summary>
    public const string AddText = "Add to desktop";
    public const string AddHelp = "Puts a shortcut on your desktop that runs this flow.";

    /// <summary>UX §5.7 #25.</summary>
    public const string EngineOffText = "Start Daze Control to add a desktop shortcut.";

    private volatile IReadOnlyDictionary<string, ShortcutInfo> _byFlow = new Dictionary<string, ShortcutInfo>();

    public bool EngineRunning => engine.IsRunning;

    /// <summary>The flow's shortcut, if it has one (the first, if the user copied it).</summary>
    public ShortcutInfo? Of(string flowId) => _byFlow.GetValueOrDefault(flowId);

    /// <summary>After every refresh, add or remove. Any thread.</summary>
    public event Action? Changed;

    /// <summary>Asks the engine; with no engine, no shortcuts are known.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        var answer = await engine.SendAsync(new PipeRequest { Op = Ops.Shortcuts, ConfigPath = configPath }, cancellationToken).ConfigureAwait(false);
        var byFlow = new Dictionary<string, ShortcutInfo>(StringComparer.Ordinal);
        foreach (var shortcut in answer is { Ok: true } ? answer.Shortcuts ?? [] : [])
            byFlow.TryAdd(shortcut.FlowId, shortcut);
        _byFlow = byFlow;
        Changed?.Invoke();
    }

    /// <summary>Adds the flow's shortcut, or renames it to the flow's name (Update). Null when it worked, else why not.</summary>
    public Task<string?> AddAsync(string flowId, CancellationToken cancellationToken = default) =>
        ChangeAsync(new PipeRequest { Op = Ops.CreateShortcut, Flow = flowId, ExactId = true, ConfigPath = configPath }, cancellationToken);

    public Task<string?> RemoveAsync(string flowId, CancellationToken cancellationToken = default) =>
        ChangeAsync(new PipeRequest { Op = Ops.RemoveShortcut, Flow = flowId, ExactId = true, ConfigPath = configPath }, cancellationToken);

    /// <summary>Renames waiting for the engine to load the new id (old → new); UI thread and pipe events, so locked.</summary>
    private readonly Dictionary<string, string> _moves = new(StringComparer.Ordinal);
    private bool _listening;

    /// <summary>
    /// A saved rename (D-075): once the engine has loaded the new id, the old id's shortcut is made again for it and the
    /// old one removed. Nothing to do when the old id has none. Retried on each config reload until the engine knows it.
    /// </summary>
    public Task MoveAsync(string oldId, string newId)
    {
        if (Of(oldId) is null)
            return Task.CompletedTask;
        lock (_moves)
        {
            _moves[oldId] = newId;
            if (!_listening)
            {
                engine.EventReceived += OnEngineEvent;
                _listening = true;
            }
        }
        return TryMovesAsync();
    }

    private void OnEngineEvent(PipeEvent e)
    {
        if (e.Kind == EventKinds.ConfigReloaded)
            _ = TryMovesAsync();
    }

    private async Task TryMovesAsync()
    {
        KeyValuePair<string, string>[] moves;
        lock (_moves)
            moves = [.. _moves];
        foreach (var (oldId, newId) in moves)
        {
            try
            {
                var made = await engine.SendAsync(new PipeRequest { Op = Ops.CreateShortcut, Flow = newId, ExactId = true, ConfigPath = configPath }).ConfigureAwait(false);
                if (made is not { Ok: true })
                    continue; // the engine hasn't loaded the save yet (or isn't running): the next reload tries again
                lock (_moves)
                    _moves.Remove(oldId);
                await engine.SendAsync(new PipeRequest { Op = Ops.RemoveShortcut, Flow = oldId, ExactId = true, ConfigPath = configPath }).ConfigureAwait(false);
                // Made while the old one still had the name ("Morning (2)"): again, now that the name is free.
                if (made.Shortcuts is [{ CurrentName: false }])
                    await engine.SendAsync(new PipeRequest { Op = Ops.CreateShortcut, Flow = newId, ExactId = true, ConfigPath = configPath }).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException)
            {
                // The engine went away mid-move: the next reload tries again.
            }
        }
        if (moves.Length > 0)
            await RefreshAsync().ConfigureAwait(false);
    }

    private async Task<string?> ChangeAsync(PipeRequest request, CancellationToken cancellationToken)
    {
        var answer = await engine.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return answer switch
        {
            null => EngineOffText,
            { Ok: false } => answer.Error ?? "That didn't work.",
            _ => null,
        };
    }
}
