using DazeControl.Ipc;

namespace DazeControl.Editor.ViewModels.Engine;

/// <summary>
/// The desktop shortcuts as the engine last reported them (D-049), shared by the Flows list and the flow settings so
/// a change in one shows in the other. The engine writes them; without it there's nothing to show (UX §5.7 #25).
/// </summary>
public sealed class DesktopShortcutStore(IEngineClient engine)
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
        var answer = await engine.SendAsync(new PipeRequest { Op = Ops.Shortcuts }, cancellationToken).ConfigureAwait(false);
        var byFlow = new Dictionary<string, ShortcutInfo>(StringComparer.Ordinal);
        foreach (var shortcut in answer is { Ok: true } ? answer.Shortcuts ?? [] : [])
            byFlow.TryAdd(shortcut.FlowId, shortcut);
        _byFlow = byFlow;
        Changed?.Invoke();
    }

    /// <summary>Adds the flow's shortcut, or renames it to the flow's name (Update). Null when it worked, else why not.</summary>
    public Task<string?> AddAsync(string flowId, CancellationToken cancellationToken = default) =>
        ChangeAsync(new PipeRequest { Op = Ops.CreateShortcut, Flow = flowId }, cancellationToken);

    public Task<string?> RemoveAsync(string flowId, CancellationToken cancellationToken = default) =>
        ChangeAsync(new PipeRequest { Op = Ops.RemoveShortcut, Flow = flowId }, cancellationToken);

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
