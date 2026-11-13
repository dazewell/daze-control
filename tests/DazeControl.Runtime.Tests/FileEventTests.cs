using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Runtime.Control;
using DazeControl.Runtime.Triggers;
using static DazeControl.Runtime.Tests.SchedulerTests;

namespace DazeControl.Runtime.Tests;

/// <summary>A folder watcher that watches nothing: tests say which folders exist and raise what the system would.</summary>
internal sealed class FakeFolderWatcher : IFolderWatcher
{
    private readonly Lock _lock = new();
    private readonly List<Handle> _handles = [];

    public HashSet<string> Existing { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Exists(string folder)
    {
        lock (_lock)
            return Existing.Contains(folder);
    }

    public IDisposable Watch(string folder, string? nameFilter, bool directoriesOnly, Action<FileChange, string> changed, Action broken)
    {
        lock (_lock)
        {
            if (!Existing.Contains(folder))
                throw new DirectoryNotFoundException(folder);
            var handle = new Handle(folder, nameFilter, directoriesOnly, changed, broken);
            _handles.Add(handle);
            return handle;
        }
    }

    public Handle[] All
    {
        get
        {
            lock (_lock)
                return [.. _handles];
        }
    }

    public Handle[] Live => [.. All.Where(h => !h.Disposed)];

    /// <summary>A change inside <paramref name="folder"/>, delivered to its live file watches.</summary>
    public void Raise(string folder, FileChange kind, string name)
    {
        foreach (var handle in Live.Where(h => !h.DirectoriesOnly && string.Equals(h.Folder, folder, StringComparison.OrdinalIgnoreCase)))
            handle.Changed(kind, name);
    }

    /// <summary>The guard on <paramref name="parent"/> for <paramref name="name"/> saw a folder come or go.</summary>
    public void RaiseGuard(string parent, string name, FileChange kind)
    {
        foreach (var handle in Live.Where(h => h.DirectoriesOnly && h.NameFilter == name && string.Equals(h.Folder, parent, StringComparison.OrdinalIgnoreCase)))
            handle.Changed(kind, name);
    }

    internal sealed class Handle(string folder, string? nameFilter, bool directoriesOnly, Action<FileChange, string> changed, Action broken) : IDisposable
    {
        public string Folder { get; } = folder;

        public string? NameFilter { get; } = nameFilter;

        public bool DirectoriesOnly { get; } = directoriesOnly;

        public Action<FileChange, string> Changed { get; } = changed;

        public Action Broken { get; } = broken;

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}

/// <summary>T26.5: file events through the real source, trigger service and launcher, with a fake folder watcher and a hand-moved clock.</summary>
public sealed class FileEventTests : IDisposable
{
    private const string Folder = @"C:\watch\in";

    private readonly ManualTime _time = new();
    private readonly FakeFolderWatcher _fs = new();
    private readonly FakeProvider _provider = new();
    private readonly RunHistory _history = new(null);
    private readonly FlowLauncher _launcher;
    private readonly FileEventSource _source;
    private readonly Dictionary<string, Flow> _flows = [];
    private readonly List<string> _log = [];
    private readonly List<TriggerEvent> _raised = [];
    private readonly object _gate = new();
    private readonly List<(int Target, TaskCompletionSource Done)> _waiters = [];
    private int _completed;

    public FileEventTests()
    {
        _fs.Existing.UnionWith([@"C:\", @"C:\watch", Folder]);
        _launcher = new FlowLauncher(new FlowRunner([_provider], _time), _history);
        _source = new FileEventSource(_fs, _time, _log.Add);
        _source.Raised += e =>
        {
            lock (_gate)
                _raised.Add(e);
        };
        _launcher.RunCompleted += _ =>
        {
            lock (_gate)
            {
                _completed++;
                foreach (var (target, done) in _waiters.Where(w => _completed >= w.Target).ToList())
                    done.TrySetResult();
            }
        };
    }

    public void Dispose()
    {
        _source.Dispose();
        _time.Dispose();
    }

    private sealed class Flows(FileEventTests owner) : IFlowStarter
    {
        public CalledFlow Resolve(string flowId) => owner._flows.TryGetValue(flowId, out var flow)
            ? new(flow, Library.Empty, flow.Name, null)
            : new(null, Library.Empty, flowId, "gone", CalledWhy.Missing);

        public bool IsRunning(string flowId) => owner._launcher.IsRunning(flowId);

        public StartAnswer Start(string flowId, string trigger, IReadOnlyList<string> chain, IReadOnlyDictionary<string, string> inputs, string callerName) =>
            throw new NotSupportedException();
    }

    private TriggerService Service(TriggerOptions? options = null) =>
        new([_source], _launcher, new Flows(this), _time, () => false, log: _log.Add, options: options);

    private Flow Make(string id, params EventTrigger[] triggers)
    {
        var flow = Flow(Step("ok")) with { Id = id, Name = id, Triggers = new([.. triggers]) };
        _flows[id] = flow;
        return flow;
    }

    private static EventTrigger Created(string path = Folder, string? filter = null) => new("file.created") { Path = path, Filter = filter };

    private Task Completed(int count)
    {
        lock (_gate)
        {
            if (_completed >= count)
                return Task.CompletedTask;
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((count, done));
            return done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }
    }

    private async Task ApplyAsync(TriggerService service)
    {
        service.Apply([.. _flows.Values]);
        await _source.IdleAsync();
    }

    private async Task SettleAsync(TriggerService service)
    {
        _time.Advance(FileEventSource.DefaultQuiet);
        await service.IdleAsync();
    }

    private TriggerEvent[] Events()
    {
        lock (_gate)
            return [.. _raised];
    }

    [Fact]
    public async Task ABurstOfWritesToOneFileStartsTheFlowOnce()
    {
        Make("sort", Created());
        using var service = Service();
        await ApplyAsync(service);

        _fs.Raise(Folder, FileChange.Created, "a.pdf");
        for (var i = 0; i < 20; i++)
            _fs.Raise(Folder, FileChange.Changed, "a.pdf");
        Assert.Empty(Events()); // still settling
        Assert.True(_source.HasTimer);
        await SettleAsync(service);
        await Completed(1);

        var raised = Assert.Single(Events());
        Assert.Equal("file.created", raised.Name);
        Assert.Equal(Path.Combine(Folder, "a.pdf"), raised.Detail);
        Assert.Equal(["sort"], _history.Recent.Select(r => r.FlowId));
        Assert.Equal("Event · a file appeared", _history.Recent[0].Trigger);
        Assert.False(_source.HasTimer); // nothing waits: no timer (idle = zero)
    }

    [Fact]
    public async Task ABurstKeepsWaitingWhileTheFileIsStillBeingWritten()
    {
        Make("sort", Created());
        using var service = Service();
        await ApplyAsync(service);

        _fs.Raise(Folder, FileChange.Created, "big.iso");
        for (var i = 0; i < 5; i++)
        {
            _time.Advance(FileEventSource.DefaultQuiet - TimeSpan.FromMilliseconds(100));
            _fs.Raise(Folder, FileChange.Changed, "big.iso");
        }
        Assert.Empty(Events());

        await SettleAsync(service);
        await Completed(1);
        Assert.Single(Events());
    }

    [Fact]
    public async Task EachFileInABurstGetsItsOwnOneStartAndATemporaryFileGetsNone()
    {
        Make("sort", Created());
        using var service = Service();
        await ApplyAsync(service);

        _fs.Raise(Folder, FileChange.Created, "a.txt");
        _fs.Raise(Folder, FileChange.Created, "b.txt");
        _fs.Raise(Folder, FileChange.Created, "~tmp.txt");
        _fs.Raise(Folder, FileChange.Deleted, "~tmp.txt");
        await SettleAsync(service);

        Assert.Equal(["a.txt", "b.txt"], Events().Select(e => e.File).Order());
    }

    [Theory]
    [InlineData(FileChange.Created, FileChange.Changed, FileChange.Created)]
    [InlineData(FileChange.Changed, FileChange.Changed, FileChange.Changed)]
    [InlineData(FileChange.Changed, FileChange.Deleted, FileChange.Deleted)]
    [InlineData(FileChange.Deleted, FileChange.Created, FileChange.Changed)]
    public void ABurstMergesToWhatHappenedInTheEnd(FileChange first, FileChange second, FileChange expected) =>
        Assert.Equal(expected, FileEventSource.Merge(first, second));

    [Fact]
    public async Task OnlyTheTriggerWhoseFolderAndFilterFitStarts()
    {
        Make("pdfs", Created(filter: "*.pdf"));
        Make("texts", Created(filter: "*.txt"));
        Make("elsewhere", Created(@"C:\watch\other"));
        Make("deletes", new EventTrigger("file.deleted") { Path = Folder });
        _fs.Existing.Add(@"C:\watch\other");
        using var service = Service();
        await ApplyAsync(service);

        _fs.Raise(Folder, FileChange.Created, "Report.PDF");
        await SettleAsync(service);
        await Completed(1);

        Assert.Equal(["pdfs"], _history.Recent.Select(r => r.FlowId));
        Assert.Equal(2, _source.WatchedFolders);
    }

    [Fact]
    public async Task FlowsOnOneFolderShareOneWatchAndALeavingFlowReleasesIt()
    {
        Make("a", Created());
        Make("b", new EventTrigger("file.changed") { Path = Folder + @"\" });
        using var service = Service();
        await ApplyAsync(service);
        Assert.Equal(2, _fs.Live.Length); // the folder and a guard on its parent: one watch for both flows
        Assert.Equal(1, service.RunningSources);

        _flows.Remove("a");
        await ApplyAsync(service);
        Assert.Equal(2, _fs.Live.Length);

        _flows.Remove("b");
        await ApplyAsync(service);

        Assert.Empty(_fs.Live);
        Assert.Equal(0, _source.WatchedFolders);
        Assert.Equal(0, service.RunningSources);
    }

    [Fact]
    public async Task AReloadedFlowThatChangedFolderMovesItsWatch()
    {
        Make("sort", Created());
        _fs.Existing.Add(@"C:\watch\two");
        using var service = Service();
        await ApplyAsync(service);
        var first = _fs.Live.First(h => !h.DirectoriesOnly);

        Make("sort", Created(@"C:\watch\two"));
        await ApplyAsync(service);

        Assert.True(first.Disposed);
        Assert.Equal([@"C:\watch\two"], _fs.Live.Where(h => !h.DirectoriesOnly).Select(h => h.Folder));
    }

    [Fact]
    public async Task AnEventThatArrivesAfterTheFlowWentAwayRaisesNothing()
    {
        Make("sort", Created());
        using var service = Service();
        await ApplyAsync(service);
        var handle = _fs.Live.First(h => !h.DirectoriesOnly);

        handle.Changed(FileChange.Created, "late.txt"); // pending, not yet raised
        _flows.Clear();
        await ApplyAsync(service);
        handle.Changed(FileChange.Created, "later.txt"); // the system's callback still in flight when the watch was closed
        await SettleAsync(service);

        Assert.Empty(Events());
        Assert.False(_source.HasTimer);
        Assert.Empty(_history.Recent);
    }

    [Fact]
    public async Task AFolderThatIsNotThereIsWatchedThroughItsParentAndStartsWorkingWhenItAppearsWithoutATimer()
    {
        const string Deep = @"C:\watch\in\deep";
        Make("sort", Created(Deep));
        using var service = Service();
        await ApplyAsync(service);
        // "in" exists, "deep" doesn't: a guard on "in" for the name "deep", nothing else.
        var guard = Assert.Single(_fs.Live);
        Assert.Equal((Folder, "deep"), (guard.Folder, guard.NameFilter));
        Assert.False(_source.HasTimer);

        _fs.Existing.Add(Deep);
        _fs.RaiseGuard(Folder, "deep", FileChange.Created);
        await _source.IdleAsync();
        Assert.Contains(_fs.Live, h => h.Folder == Deep && !h.DirectoriesOnly);
        _fs.Raise(Deep, FileChange.Created, "x.txt");
        await SettleAsync(service);
        await Completed(1);
        Assert.Single(Events());

        // The folder goes away again: back to waiting on its parent, and again when it returns.
        _fs.Existing.Remove(Deep);
        _fs.RaiseGuard(Folder, "deep", FileChange.Deleted);
        await _source.IdleAsync();
        Assert.Equal((Folder, "deep"), Assert.Single(_fs.Live) is var g ? (g.Folder, g.NameFilter) : default);
        _fs.Existing.Add(Deep);
        _fs.RaiseGuard(Folder, "deep", FileChange.Created);
        await _source.IdleAsync();
        _fs.Raise(Deep, FileChange.Created, "y.txt");
        await SettleAsync(service);
        await Completed(2);
        Assert.Equal(2, Events().Length);
    }

    [Fact]
    public async Task AFolderSeveralLevelsDownIsFoundOneLevelAtATime()
    {
        Make("sort", Created(@"C:\watch\a\b"));
        using var service = Service();
        await ApplyAsync(service);
        Assert.Equal((@"C:\watch", "a"), Assert.Single(_fs.Live) is var g ? (g.Folder, g.NameFilter) : default);

        _fs.Existing.Add(@"C:\watch\a");
        _fs.RaiseGuard(@"C:\watch", "a", FileChange.Created);
        await _source.IdleAsync();

        Assert.Equal((@"C:\watch\a", "b"), Assert.Single(_fs.Live) is var h ? (h.Folder, h.NameFilter) : default);
    }

    [Fact]
    public async Task ADriveThatIsNotThereDisablesOnlyThatTriggerWithANoteAndTheEngineKeepsRunning()
    {
        Make("sort", Created(@"Z:\nowhere"), Created());
        using var service = Service();

        await ApplyAsync(service); // must not throw

        Assert.Contains(_log, l => l.Contains(@"Z:\nowhere", StringComparison.Ordinal) && l.Contains("isn't available", StringComparison.Ordinal));
        Assert.Contains(_fs.Live, h => h.Folder == Folder && !h.DirectoriesOnly); // the other folder still works

        _fs.Existing.UnionWith([@"Z:\"]); // the drive is back; the next configuration change tries again
        await ApplyAsync(service);
        Assert.Contains(_fs.Live, h => h.Folder == @"Z:\" && h.NameFilter == "nowhere");
    }

    [Fact]
    public async Task AFolderThatWillNotOpenIsLoggedAndSkippedNotThrown()
    {
        Make("sort", Created());
        var throwing = new ThrowingWatcher();
        using var source = new FileEventSource(throwing, _time, _log.Add);
        using var service = new TriggerService([source], _launcher, new Flows(this), _time, () => false, log: _log.Add);

        service.Apply([.. _flows.Values]);
        await source.IdleAsync();

        Assert.Contains(_log, l => l.Contains("can't watch", StringComparison.Ordinal) && l.Contains("denied", StringComparison.Ordinal));
    }

    private sealed class ThrowingWatcher : IFolderWatcher
    {
        public bool Exists(string folder) => true;

        public IDisposable Watch(string folder, string? nameFilter, bool directoriesOnly, Action<FileChange, string> changed, Action broken) =>
            throw new UnauthorizedAccessException("denied");
    }

    [Fact]
    public async Task AFlowThatWritesIntoItsOwnFolderIsStoppedWithANoteInRuns()
    {
        Make("sort", Created());
        using var service = Service(TriggerOptions.Default with { FileStartLimit = 3 });
        await ApplyAsync(service);

        for (var i = 1; i <= 3; i++)
        {
            _fs.Raise(Folder, FileChange.Created, $"out{i}.txt"); // a new name each time: the per-file cooldown can't catch it
            await SettleAsync(service);
            await Completed(i);
        }
        _fs.Raise(Folder, FileChange.Created, "out4.txt");
        await SettleAsync(service);

        Assert.Equal(4, _history.Recent.Count);
        var note = _history.Recent.Single(r => r.Outcome == FlowOutcome.Cancelled);
        Assert.Contains("started it 3 times within a minute", note.Nodes[0].Error, StringComparison.Ordinal);
        Assert.Contains(_log, l => l.Contains("not started", StringComparison.Ordinal));

        // A quiet minute and it is allowed to run again.
        _time.Advance(TimeSpan.FromMinutes(2));
        _fs.Raise(Folder, FileChange.Created, "later.txt");
        await SettleAsync(service);
        await Completed(4);
    }

    [Fact]
    public async Task AFolderThatKeepsFlappingStopsBeingRearmed()
    {
        Make("sort", Created());
        using var service = Service();
        await ApplyAsync(service);

        for (var i = 0; i < 12; i++)
        {
            foreach (var guard in _fs.Live.Where(h => h.DirectoriesOnly).ToList())
                guard.Broken();
            await _source.IdleAsync();
        }

        Assert.Contains(_log, l => l.Contains("keeps changing", StringComparison.Ordinal));
        Assert.Empty(_fs.Live);

        await ApplyAsync(service); // a configuration change tries again
        Assert.NotEmpty(_fs.Live);
    }

    [Fact]
    public async Task ALongPathIsCappedAndNeverReadAsAnythingButText()
    {
        var name = new string('x', 2000) + " & calc.exe";
        Make("sort", Created());
        using var service = Service();
        await ApplyAsync(service);

        _fs.Raise(Folder, FileChange.Created, name);
        _time.Advance(FileEventSource.DefaultQuiet);

        var raised = Assert.Single(Events());
        Assert.Equal(RunValues.MaxValueLength, raised.Detail!.Length);
        Assert.Equal(name, raised.File); // the filter reads the full name
        await service.IdleAsync();
    }

    [Fact]
    public async Task TheServiceDoesNotLoadTheSourceUntilAFlowSubscribes()
    {
        using var service = Service();
        Make("other", new EventTrigger("session.lock"));
        await ApplyAsync(service);

        Assert.Equal(0, service.RunningSources);
        Assert.Empty(_fs.All);
        Assert.False(_source.HasTimer);
    }
}

/// <summary>T26.5: the one test on a real folder: the system's own notifications reach a flow. Waits on completion, never on a fixed delay.</summary>
public sealed class FileEventRealFolderTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "daze-t265-" + Guid.NewGuid().ToString("N"));

    public FileEventRealFolderTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, true);
        }
        catch (IOException)
        {
        }
    }

    private static async Task<string> WriteUntilRaisedAsync(string folder, string prefix, Task<TriggerEvent> raised, CancellationToken cancellation)
    {
        // The watch may still be opening, so a first write can be missed: write a new file until one is seen.
        for (var i = 0; !raised.IsCompleted; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(folder, $"{prefix}{i}.txt"), "x", cancellation);
            await Task.WhenAny(raised, Task.Delay(300, cancellation));
        }
        return (await raised).File!;
    }

    [Fact]
    public async Task ARealFolderStartsAFlowAndSurvivesBeingRemovedAndComingBack()
    {
        var cancellation = TestContext.Current.CancellationToken;
        var inner = Path.Combine(_folder, "in");
        using var source = new FileEventSource(new SystemFolderWatcher(), TimeProvider.System, quiet: TimeSpan.FromMilliseconds(100));
        TaskCompletionSource<TriggerEvent> Expect(string prefix)
        {
            var seen = new TaskCompletionSource<TriggerEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
            source.Raised += e =>
            {
                if (e.File!.StartsWith(prefix, StringComparison.Ordinal))
                    seen.TrySetResult(e);
            };
            return seen;
        }

        var next = Expect("a");
        source.Subscribe([new EventTrigger("file.created") { Path = inner }]);
        source.Load();
        await source.IdleAsync();

        // Not there yet: it is waited for through the parent, and the folder appearing arms the watch.
        Directory.CreateDirectory(inner);
        var first = await WriteUntilRaisedAsync(inner, "a", next.Task, cancellation).WaitAsync(TimeSpan.FromSeconds(30), cancellation);
        Assert.StartsWith("a", first, StringComparison.Ordinal);
        Assert.True(TriggerText.IsFileEvent((await next.Task).Name)); // a watch that opened mid-write sees "changed" instead of "created"

        // Removed and back: the trigger works again, with no timer between.
        Directory.Delete(inner, true);
        next = Expect("b");
        Directory.CreateDirectory(inner);
        var second = await WriteUntilRaisedAsync(inner, "b", next.Task, cancellation).WaitAsync(TimeSpan.FromSeconds(30), cancellation);
        Assert.StartsWith("b", second, StringComparison.Ordinal);
    }
}
