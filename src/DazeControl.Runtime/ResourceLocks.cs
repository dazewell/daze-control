namespace DazeControl.Runtime;

/// <summary>
/// Engine-wide named locks (FEATURES F5): steps that touch the same thing ("audio", "bluetooth:technics") run one
/// at a time across all flows. Names are taken in ordinal order, so two steps can't deadlock each other.
/// One semaphore per name lives for the engine's life; names come from the catalog and config, so few.
/// </summary>
public sealed class ResourceLocks
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, SemaphoreSlim> _semaphores = [];

    /// <summary>Waits for every name; dispose the result to release them. On cancellation nothing stays held.</summary>
    public async Task<IDisposable> AcquireAsync(IEnumerable<string> names, CancellationToken cancellationToken)
    {
        var held = new List<SemaphoreSlim>();
        try
        {
            foreach (var name in names.Distinct().Order(StringComparer.Ordinal))
            {
                var semaphore = Get(name);
                await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                held.Add(semaphore);
            }
        }
        catch
        {
            Release(held);
            throw;
        }
        return new Releaser(held);
    }

    private SemaphoreSlim Get(string name)
    {
        lock (_lock)
        {
            if (!_semaphores.TryGetValue(name, out var semaphore))
                _semaphores[name] = semaphore = new SemaphoreSlim(1, 1);
            return semaphore;
        }
    }

    private static void Release(List<SemaphoreSlim> held)
    {
        for (var i = held.Count - 1; i >= 0; i--)
            held[i].Release();
        held.Clear();
    }

    private sealed class Releaser(List<SemaphoreSlim> held) : IDisposable
    {
        public void Dispose()
        {
            lock (held)
                Release(held); // second Dispose finds the list empty
        }
    }
}
