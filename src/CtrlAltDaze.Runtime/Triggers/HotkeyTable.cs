using CtrlAltDaze.Core.Input;
using CtrlAltDaze.Core.Model;

namespace CtrlAltDaze.Runtime.Triggers;

/// <summary>The OS side of global shortcuts (the engine's <c>RegisterHotKey</c> wrapper; a fake in tests).</summary>
public interface IHotkeyRegistrar
{
    /// <summary>Null on success, else why not, in plain words.</summary>
    string? Register(int id, Hotkey hotkey);

    void Unregister(int id);
}

/// <summary>
/// Which shortcut runs which flow (FEATURES F2): built from the runnable flows on every config change, applied as a
/// diff so unchanged chords stay registered. One chord that can't be registered (another app owns it) fails alone
/// and is retried on the next change (T3.3) and on <see cref="RetryFailures"/> (user actions, D-072: no timer).
/// <see cref="Paused"/> (tray "Pause shortcuts") and <see cref="Recording"/> (the editor's shortcut box has focus)
/// each unregister everything until they end; a failure stays known meanwhile, so its note doesn't flicker. Not
/// thread-safe: the engine calls it on its message thread.
/// </summary>
public sealed class HotkeyTable(IHotkeyRegistrar registrar)
{
    private readonly Dictionary<Hotkey, string> _wanted = [];
    private readonly Dictionary<Hotkey, int> _registered = [];
    private readonly Dictionary<int, Hotkey> _byId = [];
    private readonly Dictionary<Hotkey, string> _failures = [];
    private readonly List<string> _invalid = [];
    private int _nextId;

    public bool Paused { get; private set; }

    /// <summary>The editor records a shortcut (pipe <c>record-hotkey</c>): its own flag, apart from Pause, not shown in the tray.</summary>
    public bool Recording { get; private set; }

    /// <summary><see cref="Failures"/> changed: a chord failed, was freed and registered, or isn't wanted anymore.</summary>
    public event Action? FailuresChanged;

    /// <summary>Chords that couldn't be registered, with why (for the tooltip, <c>cad status</c>, the editor).</summary>
    public IReadOnlyDictionary<Hotkey, string> Failures => _failures;

    /// <summary>Shortcut texts that don't parse, as "flow id: text" (validation reports them too).</summary>
    public IReadOnlyList<string> Invalid => _invalid;

    /// <summary>Every chord a flow wants, registered or not.</summary>
    public IReadOnlyDictionary<Hotkey, string> Wanted => _wanted;

    /// <summary>Takes the flows' shortcuts; the first flow that claims a chord gets it (validation fails the later one).</summary>
    public void Apply(IEnumerable<Flow> runnableFlows)
    {
        _wanted.Clear();
        _invalid.Clear();
        foreach (var flow in runnableFlows)
        {
            foreach (var text in flow.Hotkeys)
            {
                if (!Hotkey.TryParse(text, out var hotkey))
                    _invalid.Add($"{flow.Id}: {text}");
                else
                    _wanted.TryAdd(hotkey, flow.Id);
            }
        }
        Sync();
    }

    public void Pause()
    {
        Paused = true;
        Sync();
    }

    public void Resume()
    {
        Paused = false;
        Sync();
    }

    public void StartRecording()
    {
        Recording = true;
        Sync();
    }

    /// <summary>Registers every wanted chord again, the failed ones too.</summary>
    public void StopRecording()
    {
        Recording = false;
        Sync();
    }

    /// <summary>Tries the failed chords again (the tray menu opens): one that another app let go of is registered now.</summary>
    public void RetryFailures() => Sync();

    /// <summary>Why a chord <c>RegisterHotKey</c> refused as taken (1409) can't be used: Windows' own (H5) or another app's (H6).</summary>
    public static string TakenMessage(Hotkey hotkey) => WindowsShortcuts.Contains(hotkey)
        ? $"Windows uses {hotkey}. Try another one."
        : $"Another app uses {hotkey}. If you closed it, check the tray: it may still be running. Try another one.";

    /// <summary>The flow a <c>WM_HOTKEY</c> id runs now, with its chord; null for an id that's gone.</summary>
    public (string FlowId, Hotkey Hotkey)? Resolve(int id) =>
        _byId.TryGetValue(id, out var hotkey) && _wanted.TryGetValue(hotkey, out var flowId) ? (flowId, hotkey) : null;

    /// <summary>The shortcut shown next to a flow in the tray menu: its first registered chord, else its first wanted one.</summary>
    public Hotkey? ShortcutOf(string flowId)
    {
        Hotkey? fallback = null;
        foreach (var (hotkey, owner) in _wanted)
        {
            if (owner != flowId)
                continue;
            if (_registered.ContainsKey(hotkey))
                return hotkey;
            fallback ??= hotkey;
        }
        return fallback;
    }

    /// <summary>Unregisters everything (engine exit).</summary>
    public void Clear()
    {
        _wanted.Clear();
        Sync();
    }

    private void Sync()
    {
        var before = _failures.Count == 0 ? null : _failures.ToList();
        var keep = Paused || Recording ? [] : _wanted.Keys.ToHashSet();
        foreach (var (hotkey, id) in _registered.Where(r => !keep.Contains(r.Key)).ToList())
        {
            registrar.Unregister(id);
            _registered.Remove(hotkey);
            _byId.Remove(id);
        }
        foreach (var hotkey in _failures.Keys.Where(h => !_wanted.ContainsKey(h)).ToList())
            _failures.Remove(hotkey);
        foreach (var hotkey in keep.Where(h => !_registered.ContainsKey(h)))
        {
            var id = ++_nextId;
            if (registrar.Register(id, hotkey) is { } failure)
            {
                _failures[hotkey] = failure;
                continue;
            }
            _failures.Remove(hotkey);
            _registered[hotkey] = id;
            _byId[id] = hotkey;
        }
        if (!SameFailures(before))
            FailuresChanged?.Invoke();
    }

    private bool SameFailures(List<KeyValuePair<Hotkey, string>>? before) => before is null
        ? _failures.Count == 0
        : before.Count == _failures.Count
            && before.All(f => _failures.TryGetValue(f.Key, out var why) && why == f.Value);
}
