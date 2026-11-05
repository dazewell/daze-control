using DazeControl.Core.Input;
using DazeControl.Core.Model;

namespace DazeControl.Runtime.Triggers;

/// <summary>
/// Which controller chord runs which flow (DESIGN §2.4, §3.4), the pad twin of <see cref="HotkeyTable"/>: rebuilt from
/// the runnable flows on every config change and applied as a diff, so a press in progress survives a reload that
/// left its chord alone. <see cref="Paused"/> (tray "Pause shortcuts") and <see cref="Recording"/> (the editor's
/// <c>record-gamepad</c>) each silence every chord until they end. <see cref="NeedsSource"/> tells the engine whether
/// the gamepad source must run at all. Not thread-safe: the engine feeds it from one place.
/// </summary>
public sealed class GamepadTable
{
    private readonly GamepadChordRecognizer _recognizer = new();
    private readonly Dictionary<GamepadChord, string> _wanted = [];
    private readonly List<string> _invalid = [];
    private HashSet<GamepadButtons> _active = [];

    public bool Paused { get; private set; }

    /// <summary>The editor records a chord: apart from Pause, not shown in the tray.</summary>
    public bool Recording { get; private set; }

    /// <summary>Chords the pad is listening for now changed (the engine starts or stops the source).</summary>
    public event Action? ActiveChanged;

    /// <summary>Every chord a flow wants, listening or not.</summary>
    public IReadOnlyDictionary<GamepadChord, string> Wanted => _wanted;

    /// <summary>Chord texts that don't parse, as "flow id: text" (validation reports them too).</summary>
    public IReadOnlyList<string> Invalid => _invalid;

    /// <summary>Some chord is listening, so the source must run.</summary>
    public bool NeedsSource => _active.Count > 0;

    /// <summary>The first flow that claims a chord gets it (validation fails the later one).</summary>
    public void Apply(IEnumerable<Flow> runnableFlows)
    {
        _wanted.Clear();
        _invalid.Clear();
        foreach (var flow in runnableFlows)
        {
            foreach (var text in flow.Gamepad)
            {
                if (!GamepadChord.TryParse(text, out var chord))
                    _invalid.Add($"{flow.Id}: {text}");
                else
                    _wanted.TryAdd(chord, flow.Id);
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

    public void StopRecording()
    {
        Recording = false;
        Sync();
    }

    /// <summary>Feeds the buttons now held; returns the flow whose chord was just completed and released, if any.</summary>
    public (string FlowId, GamepadChord Chord)? Feed(GamepadButtons held)
    {
        if (Paused || Recording || _recognizer.Update(held) is not { } fired)
            return null;
        var chord = new GamepadChord(fired);
        return _wanted.TryGetValue(chord, out var flowId) ? (flowId, chord) : null;
    }

    /// <summary>The pad went away: forget a press in progress.</summary>
    public void Reset() => _recognizer.Reset();

    /// <summary>The flow shown next to nothing in the tray: its first wanted chord.</summary>
    public GamepadChord? ChordOf(string flowId) => _wanted.FirstOrDefault(w => w.Value == flowId).Key;

    private void Sync()
    {
        HashSet<GamepadButtons> now = Paused || Recording ? [] : [.. _wanted.Keys.Select(c => c.Buttons)];
        if (now.SetEquals(_active))
            return;
        _active = now;
        _recognizer.SetChords(now);
        if (now.Count == 0)
            _recognizer.Reset();
        ActiveChanged?.Invoke();
    }
}
