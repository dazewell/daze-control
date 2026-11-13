namespace DazeControl.Providers.Basic;

/// <summary>
/// Shows <c>notify</c> text one at a time (D-132). Windows keeps a single balloon per tray icon, so a second one posted
/// while the first is on screen replaces it before it can be read. The first shows at once; each next one waits
/// <see cref="Gap"/> after the previous. The timer exists only while something is waiting (idle = zero, D-009).
/// </summary>
public sealed class NoticeSpacer(Action<string, string, bool> show, TimeProvider time) : INotifier, IDisposable
{
    /// <summary>How long a notice stays alone on screen before the next one replaces it.</summary>
    public static readonly TimeSpan Gap = TimeSpan.FromSeconds(5);

    private const int MaxWaiting = 20;

    private readonly Lock _gate = new();
    private readonly Queue<(string Flow, string Text, bool Sound)> _waiting = new();
    private ITimer? _timer;
    private bool _busy;

    public void Show(string flowName, string text, bool sound)
    {
        lock (_gate)
        {
            if (_busy)
            {
                if (_waiting.Count >= MaxWaiting)
                    _waiting.Dequeue();
                _waiting.Enqueue((flowName, text, sound));
                return;
            }
            _busy = true;
            Arm();
        }
        show(flowName, text, sound);
    }

    private void Arm()
    {
        _timer?.Dispose();
        _timer = time.CreateTimer(_ => Next(), null, Gap, Timeout.InfiniteTimeSpan);
    }

    private void Next()
    {
        (string Flow, string Text, bool Sound) item;
        lock (_gate)
        {
            if (!_waiting.TryDequeue(out item))
            {
                _busy = false;
                _timer?.Dispose();
                _timer = null;
                return;
            }
            Arm();
        }
        show(item.Flow, item.Text, item.Sound);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _timer?.Dispose();
            _timer = null;
            _waiting.Clear();
        }
    }
}
