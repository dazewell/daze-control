using System.Security.Principal;

namespace DazeControl.Engine.Lifecycle;

/// <summary>
/// One engine per user (FEATURES F9), across all their sessions: a named mutex in the <c>Global\</c> namespace keyed
/// by the user SID — the same scope as the pipe (named pipes are machine-wide), so history and logs have one writer.
/// Plain users may create <c>Global\</c> mutexes. Held until disposed or the process dies (Windows abandons it
/// then, and the next engine takes it).
/// </summary>
internal sealed class SingleInstance : IDisposable
{
    public static string MutexName(string userSid) => $@"Global\DazeControl.Engine-{userSid}";

    public static string CurrentMutexName()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return MutexName(identity.User!.Value);
    }

    private readonly Mutex _mutex;
    private bool _owned;

    private SingleInstance(Mutex mutex, bool owned)
    {
        _mutex = mutex;
        _owned = owned;
    }

    /// <summary>Null when another engine of this user already holds it.</summary>
    public static SingleInstance? TryAcquire(string? name = null)
    {
        name ??= CurrentMutexName();
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
