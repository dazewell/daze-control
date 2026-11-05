using DazeControl.Core.Input;

namespace DazeControl.Runtime.Triggers;

/// <summary>The thing that reads pads (the engine's XInput thread; tests fake it). Nothing runs before <see cref="Start"/>.</summary>
public interface IPadBackend : IDisposable
{
    /// <summary>A reading per poll while a pad is on, from the reader's thread.</summary>
    event Action<PadReading>? Reading;

    /// <summary>Starts the reader and its arrival notifications (D-056); the reader parks itself with no pad on.</summary>
    void Start();

    void Halt();

    /// <summary>60 Hz while a menu is open, 10 Hz otherwise (D-056).</summary>
    void SetFast(bool fast);

    /// <summary>The pads now on; one enumeration, the reader needn't run.</summary>
    IReadOnlyList<PadInfo> List();
}

/// <summary>What <c>record-gamepad</c> found: a chord in canonical text, or why not.</summary>
public sealed record GamepadRecord(string? Chord, string? Error)
{
    public const string NoController = "No controller.";
    public const string TimedOut = "Timed out.";
    public const string Stopped = "Stopped.";
}

/// <summary>What the pipe ops need from the gamepad side (EngineController stays free of Win32).</summary>
public interface IGamepadControl
{
    IReadOnlyList<PadInfo> List();

    /// <summary>The next chord the player makes; ends on the answer, after the timeout, or when the token does.</summary>
    Task<GamepadRecord> RecordAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Who wants pad readings, so the source runs only for them (DESIGN §3.4): chords listening, an open menu, a
/// <c>record-gamepad</c> in progress. The first listener starts the backend and the last one's disposal stops it; with
/// none the engine never loads XInput. Thread-safe.
/// </summary>
public sealed class GamepadService(IPadBackend backend, TimeProvider time) : IGamepadControl, IDisposable
{
    public static readonly TimeSpan RecordTimeout = TimeSpan.FromSeconds(15);

    private readonly object _lock = new();
    private readonly List<Listener> _listeners = [];
    private volatile Listener[] _snapshot = []; // readings never take the lock, so Halt (which joins the reader) can't wait on it
    private bool _suspended;
    private bool _started;
    private int _recordings;

    /// <summary>True when the first recording starts, false when the last one ends (the engine silences chords meanwhile).</summary>
    public event Action<bool>? RecordingChanged;

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

    /// <summary>Delivers readings (a poll each) until disposed. <paramref name="fast"/> asks for the menu's 60 Hz.</summary>
    public IDisposable Listen(Action<PadReading> onReading, bool fast = false)
    {
        var listener = new Listener(this, onReading, fast);
        lock (_lock)
        {
            _listeners.Add(listener);
            Update();
        }
        return listener;
    }

    public IReadOnlyList<PadInfo> List() => backend.List();

    /// <summary>Sleep: the reader stops (listeners stay); <see cref="Resume"/> brings it back.</summary>
    public void Suspend()
    {
        lock (_lock)
        {
            _suspended = true;
            Update();
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            _suspended = false;
            Update();
        }
    }

    public async Task<GamepadRecord> RecordAsync(CancellationToken cancellationToken)
    {
        if (backend.List().Count == 0)
            return new(null, GamepadRecord.NoController);
        var done = new TaskCompletionSource<GamepadRecord>(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = GamepadButtons.None;
        var seenUp = false;
        if (Interlocked.Increment(ref _recordings) == 1)
            RecordingChanged?.Invoke(true);
        try
        {
            using var listening = Listen(reading =>
            {
                var now = reading.Buttons;
                // A button already down when recording began doesn't count until everything was seen up.
                if (!seenUp)
                {
                    held = now;
                    seenUp = now == GamepadButtons.None;
                    return;
                }
                if (now == held)
                    return;
                if (held != GamepadButtons.None && (held & ~now) != GamepadButtons.None)
                    done.TrySetResult(new(new GamepadChord(held).ToString(), null));
                held = now;
            });
            using var timeout = new CancellationTokenSource(RecordTimeout, time);
            using var link = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            using (link.Token.Register(() => done.TrySetResult(new(null,
                cancellationToken.IsCancellationRequested ? GamepadRecord.Stopped : GamepadRecord.TimedOut))))
            {
                return await done.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            if (Interlocked.Decrement(ref _recordings) == 0)
                RecordingChanged?.Invoke(false);
        }
    }

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
        var want = _listeners.Count > 0 && !_suspended;
        if (want)
        {
            backend.SetFast(_listeners.Any(l => l.Fast));
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

    private sealed class Listener(GamepadService owner, Action<PadReading> callback, bool fast) : IDisposable
    {
        public bool Fast { get; } = fast;

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
