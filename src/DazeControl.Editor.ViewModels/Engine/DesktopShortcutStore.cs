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

    /// <summary>Saved renames whose shortcut hasn't moved yet (old → new); UI thread and pipe events, so locked.</summary>
    private readonly Dictionary<string, string> _moves = new(StringComparer.Ordinal);
    private bool _moving, _again; // one pass at a time; an event during a pass asks for another
    private bool _listening;

    /// <summary>
    /// A saved rename (D-075): once the engine has loaded the new id, the old id's shortcut is made again for it and the
    /// old one removed. Kept until the old one is gone (or there was none), and retried on each config reload and each
    /// engine start, so an engine that is off or goes away mid-move doesn't strand it.
    /// </summary>
    public Task MoveAsync(string oldId, string newId)
    {
        lock (_moves)
        {
            // A second rename before the first moved: the original shortcut goes straight to the newest id.
            foreach (var pending in _moves.Where(m => m.Value == oldId).ToList())
                _moves[pending.Key] = newId;
            _moves[oldId] = newId;
            if (!_listening)
            {
                engine.EventReceived += OnEngineEvent;
                engine.StateChanged += OnEngineStateChanged;
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

    private void OnEngineStateChanged()
    {
        if (engine.IsRunning)
            _ = TryMovesAsync();
    }

    private async Task TryMovesAsync()
    {
        lock (_moves)
        {
            if (_moving)
            {
                _again = true;
                return;
            }
            _moving = true;
        }
        try
        {
            bool again;
            do
            {
                lock (_moves)
                    _again = false;
                await MovePassAsync().ConfigureAwait(false);
                lock (_moves)
                    again = _again;
            }
            while (again);
        }
        finally
        {
            lock (_moves)
                _moving = false;
        }
    }

    private async Task MovePassAsync()
    {
        try
        {
            KeyValuePair<string, string>[] moves;
            lock (_moves)
                moves = [.. _moves];
            if (moves.Length == 0)
                return;
            // Asked of the engine, not the cached list: a shortcut made by dctl or another editor counts too.
            var listed = await engine.SendAsync(new PipeRequest { Op = Ops.Shortcuts, ConfigPath = configPath }).ConfigureAwait(false);
            if (listed is not { Ok: true })
                return; // engine off: its next start tries again
            var have = (listed.Shortcuts ?? []).Select(s => s.FlowId).ToHashSet(StringComparer.Ordinal);
            foreach (var (oldId, newId) in moves)
            {
                if (!have.Contains(oldId))
                {
                    Done(oldId, newId); // nothing (left) to move
                    continue;
                }
                var made = await Send(Ops.CreateShortcut, newId).ConfigureAwait(false);
                if (made is not { Ok: true })
                    continue; // the engine hasn't loaded the save yet: the next reload tries again
                if (await Send(Ops.RemoveShortcut, oldId).ConfigureAwait(false) is not { Ok: true })
                    continue; // still pending: the old one may still be there
                Done(oldId, newId);
                // Made while the old one still had the name ("Morning (2)"): again, now that the name is free.
                if (made.Shortcuts is [{ CurrentName: false }])
                    await Send(Ops.CreateShortcut, newId).ConfigureAwait(false);
            }
            await RefreshAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException)
        {
            // The engine went away mid-move: the move stays pending for its next start.
        }
    }

    private Task<PipeMessage?> Send(string op, string flowId) =>
        engine.SendAsync(new PipeRequest { Op = op, Flow = flowId, ExactId = true, ConfigPath = configPath });

    /// <summary>Drops the move unless a newer rename replaced it meanwhile.</summary>
    private void Done(string oldId, string newId)
    {
        lock (_moves)
        {
            if (_moves.TryGetValue(oldId, out var pending) && pending == newId)
                _moves.Remove(oldId);
        }
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
