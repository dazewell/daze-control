namespace DazeControl.Providers.Basic;

/// <summary>
/// Shows <c>notify</c> text one at a time (D-132). Windows keeps a single balloon per tray icon, so a second one posted
/// while the first is on screen replaces it before it can be read. The first shows at once; each next one waits
/// <see cref="Gap"/> after the previous. The timer exists only while a notice is waiting (idle = zero, D-009).
/// </summary>
public sealed class NoticeSpacer(Action<string, string, bool> show, TimeProvider time) : INotifier, IDisposable
{
    /// <summary>How long a notice stays alone on screen before the next one replaces it.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(5);

    private const int MaxWaiting = 20;

    private readonly Lock _gate = new();
    private readonly Queue<(string Flow, string Text, bool Sound)> _waiting = new();
    private long _freeAt; // timestamp when the notice on screen has had its gap
    private bool _shownAny;
    private ITimer? _timer;
    private bool _disposed;

    public void Show(string flowName, string text, bool sound)
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            var now = time.GetTimestamp();
            if (_shownAny && (_waiting.Count > 0 || now < _freeAt))
            {
                if (_waiting.Count >= MaxWaiting)
                    _waiting.Dequeue();
                _waiting.Enqueue((flowName, text, sound));
                _timer ??= time.CreateTimer(_ => Next(), null, TimeSpan.FromTicks(Math.Max(0, time.GetElapsedTime(now, _freeAt).Ticks)), Timeout.InfiniteTimeSpan);
                return;
            }
            _shownAny = true;
            _freeAt = now + (long)(Gap.TotalSeconds * time.TimestampFrequency);
        }
        show(flowName, text, sound);
    }

    private void Next()
    {
        (string Flow, string Text, bool Sound) item;
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            if (_disposed || !_waiting.TryDequeue(out item))
                return;
            var now = time.GetTimestamp();
            _freeAt = now + (long)(Gap.TotalSeconds * time.TimestampFrequency);
            if (_waiting.Count > 0)
                _timer = time.CreateTimer(_ => Next(), null, Gap, Timeout.InfiniteTimeSpan);
        }
        show(item.Flow, item.Text, item.Sound);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _timer?.Dispose();
            _timer = null;
            _waiting.Clear();
        }
    }
}
