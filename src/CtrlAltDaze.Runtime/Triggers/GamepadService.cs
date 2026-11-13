using CtrlAltDaze.Core.Input;

namespace CtrlAltDaze.Runtime.Triggers;

/// <summary>The thing that reads pads (the engine's XInput thread; tests fake it). Nothing runs before <see cref="Start"/>.</summary>
public interface IPadBackend : IDisposable
{
    /// <summary>A reading per poll while a pad is on, from the reader's thread.</summary>
    event Action<PadReading>? Reading;

    /// <summary>Starts the reader and its arrival notifications (D-056); the reader parks itself with no pad on.</summary>
    void Start();

    void Halt();

    /// <summary>The pads now on; one enumeration, the reader needn't run.</summary>
    IReadOnlyList<PadInfo> List();
}

/// <summary>What the pipe ops need from the gamepad side (EngineController stays free of Win32).</summary>
public interface IGamepadControl
{
    IReadOnlyList<PadInfo> List();
}

/// <summary>
/// Who wants pad readings, so the source runs only for them (D-086): an open menu. The first listener starts the
/// backend and the last one's disposal stops it; with none the engine never loads XInput. Thread-safe.
/// </summary>
public sealed class GamepadService(IPadBackend backend) : IGamepadControl, IDisposable
{
    private readonly object _lock = new();
    private readonly List<Listener> _listeners = [];
    private volatile Listener[] _snapshot = []; // readings never take the lock, so Halt (which joins the reader) can't wait on it
    private bool _started;

    public bool Running
    {
        get
        {
            lock (_lock)
                return _started;
        }
    }

    public int Listeners
    {
        get
        {
            lock (_lock)
                return _listeners.Count;
        }
    }

    /// <summary>Delivers readings (a poll each, 60 Hz) until disposed.</summary>
    public IDisposable Listen(Action<PadReading> onReading)
    {
        var listener = new Listener(this, onReading);
        lock (_lock)
        {
            _listeners.Add(listener);
            Update();
        }
        return listener;
    }

    public IReadOnlyList<PadInfo> List() => backend.List();

    public void Dispose()
    {
        lock (_lock)
        {
            _listeners.Clear();
            Update();
        }
        backend.Dispose();
    }

    private void Remove(Listener listener)
    {
        lock (_lock)
        {
            if (_listeners.Remove(listener))
                Update();
        }
    }

    private void OnReading(PadReading reading)
    {
        foreach (var listener in _snapshot)
            listener.OnReading(reading);
    }

    /// <summary>Under the lock: starts or stops the backend to match the listeners.</summary>
    private void Update()
    {
        _snapshot = [.. _listeners];
        if (_listeners.Count > 0)
        {
            if (!_started)
            {
                _started = true;
                backend.Reading += OnReading;
                backend.Start();
            }
        }
        else if (_started)
        {
            _started = false;
            backend.Reading -= OnReading;
            backend.Halt();
        }
    }

    private sealed class Listener(GamepadService owner, Action<PadReading> callback) : IDisposable
    {
        public void OnReading(PadReading reading)
        {
            try
            {
                callback(reading);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // One listener's failure must not stop the others (fail-soft).
            }
        }

        public void Dispose() => owner.Remove(this);
    }
}
