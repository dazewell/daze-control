using DazeControl.Core.Input;
using DazeControl.Core.Model;

namespace DazeControl.Runtime.Triggers;

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
/// and is retried on the next change (T3.3). <see cref="Paused"/> unregisters everything until resumed (tray
/// "Pause shortcuts"). Not thread-safe: the engine calls it on its message thread.
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

    /// <summary>Chords that couldn't be registered, with why (for the tooltip, <c>dctl status</c>, the editor).</summary>
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
        var keep = Paused ? [] : _wanted.Keys.ToHashSet();
        foreach (var (hotkey, id) in _registered.Where(r => !keep.Contains(r.Key)).ToList())
        {
            registrar.Unregister(id);
            _registered.Remove(hotkey);
            _byId.Remove(id);
        }
        foreach (var hotkey in _failures.Keys.Where(h => !keep.Contains(h)).ToList())
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
    }
}
