using DazeControl.Core.Model;
using DazeControl.Core.Runs;

namespace DazeControl.Runtime.Triggers;

/// <summary>
/// <c>file.created</c> / <c>file.changed</c> / <c>file.deleted</c> (T26.5, D-145): changes directly inside the folders some flow
/// names in <c>path:</c>. Idle = zero (D-009): nothing is watched until a flow subscribes; <see cref="Subscribe"/> and
/// <see cref="Load"/> open one watch per distinct folder, a flow that goes away, turns off or is reloaded closes its watch, and
/// the only timer is a one-shot that exists while a burst of changes waits to settle.
/// <para>
/// A folder that is missing (or on a drive that left) is watched through its nearest existing parent for the next folder
/// name to appear, so it starts working when the folder comes back, with no timer. A folder that can't be watched at all
/// (no such drive, access denied) disables only itself with a log line and is tried again on the next configuration change.
/// All IO (is it there, opening the watch) happens on the pool, never on the message thread.
/// </para>
/// <para>
/// Bursts coalesce per file: Created then Changed is one "created", Created then Deleted is nothing, Deleted then Created is one
/// "changed"; an event is raised once the file has been quiet for <c>quiet</c> (or <c>maxWait</c> after its first change, so a file
/// written forever still reports). Every callback carries the watch's epoch, so an event that arrives after the folder was
/// retired, re-armed or the source unloaded is dropped before it can raise anything.
/// </para>
/// </summary>
public sealed class FileEventSource : IConfiguredTriggerSource
{
    public static readonly IReadOnlyList<string> Names = ["file.created", "file.changed", "file.deleted"];

    public static readonly TimeSpan DefaultQuiet = TimeSpan.FromMilliseconds(750);

    public static readonly TimeSpan DefaultMaxWait = TimeSpan.FromSeconds(10);

    /// <summary>A watch that had to be re-armed more than this often within <see cref="RearmWindow"/> is disabled (a folder that flaps).</summary>
    private const int MaxRearms = 8;

    private static readonly TimeSpan RearmWindow = TimeSpan.FromSeconds(10);

    private readonly IFolderWatcher _watcher;
    private readonly TimeProvider _time;
    private readonly Action<string> _log;
    private readonly TimeSpan _quiet;
    private readonly TimeSpan _maxWait;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Watch> _watches = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(Watch Watch, string Name), Pending> _pending = [];
    private readonly HashSet<Task> _tasks = [];
    private HashSet<string> _wanted = new(StringComparer.OrdinalIgnoreCase);
    private ITimer? _timer;
    private bool _loaded;
    private bool _disposed;

    public FileEventSource(IFolderWatcher watcher, TimeProvider time, Action<string>? log = null, TimeSpan? quiet = null, TimeSpan? maxWait = null)
    {
        _watcher = watcher;
        _time = time;
        _log = log ?? (_ => { });
        _quiet = quiet ?? DefaultQuiet;
        _maxWait = maxWait ?? DefaultMaxWait;
    }

    public IReadOnlyList<string> Events => Names;

    public event Action<TriggerEvent>? Raised;

    /// <summary>Folders with a watch now, armed or waiting for their parent (tests, status).</summary>
    public int WatchedFolders
    {
        get
        {
            lock (_lock)
                return _watches.Count;
        }
    }

    /// <summary>Whether a one-shot timer is set now: only while changes wait to settle (tests).</summary>
    public bool HasTimer
    {
        get
        {
            lock (_lock)
                return _timer is not null;
        }
    }

    public void Subscribe(IReadOnlyList<EventTrigger> triggers)
    {
        HashSet<string> wanted = new(StringComparer.OrdinalIgnoreCase);
        foreach (var trigger in triggers)
        {
            if (TriggerText.IsFileEvent(trigger.Name) && TriggerText.FileFilterProblem(trigger.Name, trigger.Path, trigger.Filter) is null
                && TriggerText.ResolveFolder(trigger.Path, out _) is { } folder)
                wanted.Add(folder);
        }
        List<IDisposable> release = [];
        List<Watch> start = [];
        lock (_lock)
        {
            if (_disposed)
                return;
            _wanted = wanted;
            if (_loaded)
                Reconcile(release, start);
        }
        Finish(release, start);
    }

    public void Load()
    {
        List<IDisposable> release = [];
        List<Watch> start = [];
        lock (_lock)
        {
            if (_loaded || _disposed)
                return;
            _loaded = true;
            Reconcile(release, start);
        }
        Finish(release, start);
    }

    public void Unload()
    {
        List<IDisposable> release = [];
        lock (_lock)
        {
            if (!_loaded)
                return;
            _loaded = false;
            foreach (var watch in _watches.Values)
                Retire(watch, release);
            _watches.Clear();
            _pending.Clear();
            RearmTimer();
        }
        Release(release);
    }

    public void Dispose()
    {
        Unload();
        lock (_lock)
            _disposed = true;
    }

    /// <summary>Completes when every watch being opened or re-opened has finished (tests).</summary>
    public async Task IdleAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (_lock)
                pending = [.. _tasks];
            if (pending.Length == 0)
                return;
            await Task.WhenAll(pending).ConfigureAwait(false);
        }
    }

    /// <summary>What a file did in one burst, or null when the burst cancels out (made and removed again).</summary>
    internal static FileChange? Merge(FileChange before, FileChange next) => (before, next) switch
    {
        (FileChange.Created, FileChange.Deleted) => null,
        (FileChange.Created, _) => FileChange.Created,
        (FileChange.Deleted, FileChange.Deleted) => FileChange.Deleted,
        (FileChange.Deleted, _) => FileChange.Changed, // replaced
        (FileChange.Changed, FileChange.Deleted) => FileChange.Deleted,
        _ => FileChange.Changed,
    };

    private static string NameOf(FileChange change) => change switch
    {
        FileChange.Created => "file.created",
        FileChange.Deleted => "file.deleted",
        _ => "file.changed",
    };

    /// <summary>Under the lock: closes watches nobody wants, adds the ones wanted, and retries those that failed.</summary>
    private void Reconcile(List<IDisposable> release, List<Watch> start)
    {
        foreach (var (folder, watch) in _watches.ToList())
        {
            if (_wanted.Contains(folder))
                continue;
            Retire(watch, release);
            _watches.Remove(folder);
        }
        foreach (var folder in _wanted)
        {
            if (!_watches.TryGetValue(folder, out var watch))
            {
                _watches[folder] = watch = new Watch(folder);
                start.Add(watch);
            }
            else if (!watch.Armed)
            {
                watch.Rearms = 0;
                start.Add(watch);
            }
        }
        foreach (var key in _pending.Keys.Where(k => !k.Watch.Live).ToList())
            _pending.Remove(key);
        RearmTimer();
    }

    /// <summary>Under the lock: the watch is over; whatever it still has in flight is dropped by its epoch.</summary>
    private static void Retire(Watch watch, List<IDisposable> release)
    {
        watch.Live = false;
        watch.Armed = false;
        watch.Epoch++;
        release.AddRange(watch.Handles);
        watch.Handles.Clear();
    }

    private void Finish(List<IDisposable> release, List<Watch> start)
    {
        Release(release);
        foreach (var watch in start)
            Spawn(watch, null);
    }

    private static void Release(List<IDisposable> handles)
    {
        foreach (var handle in handles)
        {
            try
            {
                handle.Dispose();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // a watch that is already gone
            }
        }
    }

    private void Spawn(Watch watch, int? onlyIfEpoch)
    {
        Task task;
        lock (_lock)
        {
            // Started and tracked under the lock: Arm takes it first thing, and so does the removal below, so a task that
            // finishes at once can't be removed before it is added (it would stay in the set and IdleAsync would never end).
            task = Task.Run(() => Arm(watch, onlyIfEpoch));
            _tasks.Add(task);
        }
        _ = task.ContinueWith(t =>
        {
            lock (_lock)
                _tasks.Remove(t);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>
    /// Opens the watch of one folder on the pool: the folder itself plus a guard on its parent for the folder going away, or, when
    /// the folder isn't there, a guard on the nearest parent that is, for the next folder name down. A guard event re-arms.
    /// </summary>
    private void Arm(Watch watch, int? onlyIfEpoch)
    {
        try
        {
            int epoch;
            List<IDisposable> old;
            var giveUp = false;
            lock (_lock)
            {
                if (!watch.Live || !_loaded || (onlyIfEpoch is { } expected && watch.Epoch != expected))
                    return;
                if (onlyIfEpoch is null || _time.GetElapsedTime(watch.RearmWindowStart) > RearmWindow)
                {
                    watch.RearmWindowStart = _time.GetTimestamp();
                    watch.Rearms = 0;
                }
                if (onlyIfEpoch is not null && ++watch.Rearms > MaxRearms)
                    giveUp = true; // a folder that flaps must not spin the pool
                old = [.. watch.Handles];
                watch.Handles.Clear();
                watch.Armed = false;
                epoch = ++watch.Epoch;
                // Changes seen under the old epoch belong to a folder that may be gone or another one: drop them, as the epoch drops late callbacks.
                foreach (var key in _pending.Keys.Where(k => k.Watch == watch).ToList())
                    _pending.Remove(key);
                RearmTimer();
            }
            Release(old);
            if (giveUp)
            {
                _log($"files: {watch.Folder} keeps changing under the watch; the trigger is paused until the configuration reloads");
                return;
            }

            List<IDisposable> made = [];
            string? why;
            try
            {
                why = Build(watch, epoch, made);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                why = e.Message;
            }
            if (why is not null)
            {
                Release(made);
                made = [];
            }
            bool keep;
            lock (_lock)
            {
                keep = watch.Live && _loaded && watch.Epoch == epoch;
                if (keep)
                {
                    watch.Handles.AddRange(made);
                    watch.Armed = why is null;
                }
            }
            if (!keep)
                Release(made); // retired or re-armed meanwhile: its events are already dropped by the epoch
            else if (why is not null)
                _log($"files: can't watch {watch.Folder} ({why}); the trigger is paused until the configuration reloads");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _log($"files: {watch.Folder}: {e.Message}");
        }
    }

    /// <summary>Returns null when watching, else why it can't be.</summary>
    private string? Build(Watch watch, int epoch, List<IDisposable> made)
    {
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var existing = watch.Folder;
            string? next = null;
            while (!_watcher.Exists(existing))
            {
                var parent = Path.GetDirectoryName(existing);
                if (parent is null)
                    return "the drive or network share isn't available";
                next = existing;
                existing = parent;
            }

            if (next is null)
            {
                try
                {
                    made.Add(_watcher.Watch(watch.Folder, null, false, (kind, name) => OnChange(watch, epoch, kind, name), () => Rearm(watch, epoch)));
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    if (!_watcher.Exists(watch.Folder))
                        continue; // went away between the check and the watch
                    return e.Message;
                }
                if (Path.GetDirectoryName(watch.Folder) is { } above)
                {
                    try
                    {
                        made.Add(_watcher.Watch(above, Path.GetFileName(watch.Folder), true, (_, _) => Rearm(watch, epoch), () => Rearm(watch, epoch)));
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        // No guard (no rights on the parent): the watch still works, it just can't see its folder go away.
                    }
                }
                return null;
            }

            made.Add(_watcher.Watch(existing, Path.GetFileName(next), true, (_, _) => Rearm(watch, epoch), () => Rearm(watch, epoch)));
            if (!_watcher.Exists(next))
                return null; // waiting for it
            Release([made[^1]]);
            made.RemoveAt(made.Count - 1); // it appeared before the guard was in place: look again, one step further down
        }
        return "the folder keeps changing";
    }

    private void Rearm(Watch watch, int epoch)
    {
        lock (_lock)
        {
            if (!watch.Live || !_loaded || watch.Epoch != epoch)
                return;
        }
        Spawn(watch, epoch);
    }

    private void OnChange(Watch watch, int epoch, FileChange kind, string name)
    {
        if (string.IsNullOrEmpty(name))
            return;
        lock (_lock)
        {
            if (!_loaded || !watch.Live || watch.Epoch != epoch)
                return; // a stale event: the folder was retired, re-armed or the source unloaded
            var now = _time.GetTimestamp();
            var key = (watch, name);
            if (!_pending.TryGetValue(key, out var pending))
                _pending[key] = new Pending(kind, now);
            else if (Merge(pending.Kind, kind) is { } merged)
            {
                pending.Kind = merged;
                pending.Last = now;
            }
            else
                _pending.Remove(key);
            RearmTimer();
        }
    }

    /// <summary>Under the lock: the one-shot timer fires when the first waiting file has settled; none while nothing waits (idle = zero).</summary>
    private void RearmTimer()
    {
        if (_pending.Count == 0)
        {
            _timer?.Dispose();
            _timer = null;
            return;
        }
        var now = _time.GetTimestamp();
        var wait = _pending.Values.Min(p => Min(_quiet - _time.GetElapsedTime(p.Last, now), _maxWait - _time.GetElapsedTime(p.First, now)));
        if (wait < TimeSpan.FromMilliseconds(1))
            wait = TimeSpan.FromMilliseconds(1);
        if (_timer is null)
            _timer = _time.CreateTimer(OnTimer, null, wait, Timeout.InfiniteTimeSpan);
        else
            _timer.Change(wait, Timeout.InfiniteTimeSpan);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private void OnTimer(object? _)
    {
        List<(Watch Watch, string Name, FileChange Kind)> due = [];
        lock (_lock)
        {
            if (!_loaded)
                return;
            var now = _time.GetTimestamp();
            foreach (var (key, pending) in _pending.ToList())
            {
                if (_time.GetElapsedTime(pending.Last, now) < _quiet && _time.GetElapsedTime(pending.First, now) < _maxWait)
                    continue;
                due.Add((key.Watch, key.Name, pending.Kind));
                _pending.Remove(key);
            }
            RearmTimer();
        }
        foreach (var (watch, name, kind) in due)
        {
            lock (_lock)
            {
                if (!_loaded || !watch.Live)
                    continue; // unloaded or retired between the settle and now
            }
            var full = Path.Combine(watch.Folder, name);
            // {@event} is data, never shell text; a path can be 32 K long, a value is kept to the usual length.
            var detail = full.Length > RunValues.MaxValueLength ? full[..RunValues.MaxValueLength] : full;
            try
            {
                Raised?.Invoke(new TriggerEvent(NameOf(kind), null, detail, watch.Folder, name));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                _log($"files: {full}: {e.Message}");
            }
        }
    }

    /// <summary>One watched folder; every field is guarded by the source's lock.</summary>
    private sealed class Watch(string folder)
    {
        public string Folder { get; } = folder;

        public bool Live { get; set; } = true;

        public bool Armed { get; set; }

        public int Epoch { get; set; }

        public int Rearms { get; set; }

        public long RearmWindowStart { get; set; }

        public List<IDisposable> Handles { get; } = [];
    }

    private sealed class Pending(FileChange kind, long at)
    {
        public FileChange Kind { get; set; } = kind;

        public long First { get; } = at;

        public long Last { get; set; } = at;
    }
}
