namespace DazeControl.Engine.Lifecycle;

/// <summary>
/// One engine per user session (FEATURES F9): a named mutex in the session's <c>Local\</c> namespace. Held until
/// disposed or the process dies (Windows abandons it then, and the next engine takes it).
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    public const string MutexName = @"Local\DazeControl.Engine";

    private readonly Mutex _mutex;
    private bool _owned;

    private SingleInstance(Mutex mutex, bool owned)
    {
        _mutex = mutex;
        _owned = owned;
    }

    /// <summary>Null when another engine in this session already holds it.</summary>
    public static SingleInstance? TryAcquire(string name = MutexName)
    {
        var mutex = new Mutex(initiallyOwned: false, name);
        bool owned;
        try
        {
            owned = mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // the previous engine crashed; the mutex is ours now
        }
        if (owned)
            return new SingleInstance(mutex, owned: true);
        mutex.Dispose();
        return null;
    }

    public void Dispose()
    {
        if (_owned)
        {
            _mutex.ReleaseMutex();
            _owned = false;
        }
        _mutex.Dispose();
    }
}
