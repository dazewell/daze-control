using DazeControl.Core.Model;
using DazeControl.Core.Runs;
using DazeControl.Runtime.Control;
using DazeControl.Runtime.Triggers;
using static DazeControl.Runtime.Tests.SchedulerTests;

namespace DazeControl.Runtime.Tests;

/// <summary>T20.3: the lazy event-source framework (DESIGN §3, D-107), with fake sources and a hand-moved clock.</summary>
public sealed class TriggerServiceTests : IDisposable
{
    private readonly ManualTime _time = new();
    private readonly FakeProvider _provider = new();
    private readonly RunHistory _history = new(null);
    private readonly FlowLauncher _launcher;
    private readonly FakeSource _session = new("session.lock", "session.unlock");
    private readonly FakeSource _power = new("power.ac", "power.battery");
    private readonly FakeSource _network = new("network.connected", "network.disconnected");
    private readonly FakeSource _devices = new("device.added", "device.removed", "audio.default-changed");
    private Library _library = Library.Empty;
    private readonly Dictionary<string, Flow> _known = [];
    private readonly List<string> _log = [];
    private readonly FakeCondition _onBattery = new("onBattery");
    private bool _paused;

    public TriggerServiceTests()
    {
        _launcher = new FlowLauncher(new FlowRunner([_provider], _time), _history);
    }

    public void Dispose() => _time.Dispose();

    private sealed class FakeSource(params string[] events) : ITriggerSource
    {
        public int Loads, Unloads;
        public bool Loaded;
        public bool Throws;

        public IReadOnlyList<string> Events => events;

        public event Action<TriggerEvent>? Raised;

        public void Load()
        {
            Loads++;
            Loaded = true;
            if (Throws)
                throw new InvalidOperationException("no notifications");
        }

        public void Unload()
        {
            Unloads++;
            Loaded = false;
        }

        public void Raise(string name, IReadOnlyList<string>? chain = null, string? detail = null, string? kind = null) =>
            Raised?.Invoke(new TriggerEvent(name, chain, detail, kind));

        public void Dispose()
        {
        }
    }

    private sealed class FakeCondition(string id) : ITriggerCondition
    {
        public bool Value;
        public bool Unknown;

        public string Id => id;

        public Task<bool> HoldsAsync(CancellationToken cancellationToken) =>
            Unknown ? throw new InvalidOperationException("unknown") : Task.FromResult(Value);
    }

    private sealed class FakeFlows(TriggerServiceTests owner) : IFlowStarter
    {
        public CalledFlow Resolve(string flowId) => owner._known.TryGetValue(flowId, out var flow)
            ? new(flow, owner._library, flow.Name ?? flowId, null)
            : new(null, Library.Empty, flowId, "gone", CalledWhy.Missing);

        public bool IsRunning(string flowId) => owner._launcher.IsRunning(flowId);

        public StartAnswer Start(string flowId, string trigger, IReadOnlyList<string> chain, IReadOnlyDictionary<string, string> inputs, string callerName) =>
            throw new NotSupportedException();
    }

    private TriggerService Service(TriggerOptions? options = null) =>
        new([_session, _power, _network, _devices], _launcher, new FakeFlows(this), _time, () => _paused, [_onBattery], _log.Add, options);

    private Flow On(string id, string eventName, string? only = null, WhenRunning whenRunning = WhenRunning.Ignore, string? ssid = null, NetworkVia? via = null, string? device = null)
    {
        var flow = Flow(Step("a")) with
        {
            Id = id,
            WhenRunning = whenRunning,
            Triggers = [new EventTrigger(eventName) { Only = only, Ssid = ssid, Via = via, Device = device }],
        };
        _known[id] = flow;
        return flow;
    }

    private static async Task SettleAsync(TriggerService service)
    {
        await service.IdleAsync();
        await Task.Delay(50, TestContext.Current.CancellationToken); // a run that must not start has no signal to wait for
    }

    [Fact]
    public void SourcesLoadOnlyWhileAFlowSubscribes()
    {
        using var service = Service();

        service.Apply([]);
        Assert.Equal(0, _session.Loads + _power.Loads);

        service.Apply([On("f", "session.unlock")]);
        Assert.True(_session.Loaded);
        Assert.False(_power.Loaded);

        service.Apply([On("f", "session.unlock"), On("g", "session.lock")]);
        Assert.Equal(1, _session.Loads); // still the same load

        service.Apply([On("g", "power.ac")]);
        Assert.False(_session.Loaded);
        Assert.True(_power.Loaded);

        service.Apply([]);
        Assert.False(_power.Loaded);
        Assert.Equal(1, _session.Unloads);
        Assert.Equal(1, _power.Unloads);
        Assert.Equal(0, service.RunningSources);
    }

    [Fact]
    public async Task ScreenAndAwayEventsStartFlowsAndLoadTheirSourceOnlyWhileSubscribed()
    {
        var screen = new FakeSource("display.off", "display.on", "session.away", "session.back");
        using var service = new TriggerService([_session, screen], _launcher, new FakeFlows(this), _time, () => _paused, [_onBattery], _log.Add);

        service.Apply([On("f", "display.off")]);
        Assert.True(screen.Loaded);
        var done = new TaskCompletionSource();
        _launcher.RunCompleted += _ => done.TrySetResult();
        screen.Raise("display.off");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        screen.Raise("display.on"); // nobody subscribed to it
        await SettleAsync(service);
        Assert.Equal("Event · screens turned off", Assert.Single(_history.Recent).Trigger);

        service.Apply([]);
        Assert.False(screen.Loaded);
    }

    [Fact]
    public void ABrokenSourceDisablesOnlyItself()
    {
        _session.Throws = true;
        using var service = Service();

        service.Apply([On("f", "session.unlock"), On("g", "power.ac")]);

        Assert.True(_power.Loaded);
        Assert.Contains(_log, l => l.Contains("start failed", StringComparison.Ordinal) || l.Contains("load failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASsidNarrowsNetworkConnected()
    {
        using var service = Service();
        service.Apply([On("home", "network.connected", ssid: "home wifi"), On("any", "network.connected")]);
        var started = new List<string>();
        _launcher.RunCompleted += r => { lock (started) started.Add(r.FlowId); };

        _network.Raise("network.connected", detail: "Office", kind: "wifi");
        await SettleAsync(service);
        lock (started)
            Assert.Equal(["any"], started);

        _time.Advance(TimeSpan.FromMinutes(1));
        _network.Raise("network.connected", detail: "Home WiFi", kind: "cable"); // a cable adapter named like the network isn't it
        await SettleAsync(service);
        lock (started)
            Assert.DoesNotContain("home", started);

        _time.Advance(TimeSpan.FromMinutes(1));
        _network.Raise("network.connected", detail: "Home WiFi", kind: "wifi"); // case-insensitive
        await SettleAsync(service);
        lock (started)
            Assert.Contains("home", started);
    }

    [Fact]
    public async Task AnEventStartsTheFlowAndHistorySaysWhy()
    {
        using var service = Service();
        service.Apply([On("f", "session.unlock")]);
        var done = new TaskCompletionSource();
        _launcher.RunCompleted += _ => done.TrySetResult();

        _session.Raise("session.unlock");
        await done.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal("Event · PC unlocked", Assert.Single(_history.Recent).Trigger);
    }

    [Fact]
    public async Task AnEventNobodyWantsStartsNothing()
    {
        using var service = Service();
        service.Apply([On("f", "session.unlock")]);

        _session.Raise("session.lock");
        await SettleAsync(service);

        Assert.Empty(_provider.Attempts);
    }

    [Fact]
    public async Task ABurstCountsOnce()
    {
        using var service = Service(new TriggerOptions(TimeSpan.FromSeconds(2), TimeSpan.Zero, TimeSpan.FromSeconds(5)));
        service.Apply([On("f", "session.unlock")]);

        _session.Raise("session.unlock");
        await service.IdleAsync();
        await _provider.Started("a");
        _time.Advance(TimeSpan.FromSeconds(1));
        _session.Raise("session.unlock");
        _time.Advance(TimeSpan.FromSeconds(1.5)); // 1.5 s after the latest event: still the burst
        _session.Raise("session.unlock");
        await SettleAsync(service);

        Assert.Single(_provider.Attempts);

        _time.Advance(TimeSpan.FromSeconds(3)); // quiet for longer than the debounce: a new burst
        _session.Raise("session.unlock");
        await _provider.Started("a");
        await SettleAsync(service);
        Assert.Equal(2, _provider.Attempts.Count);
    }

    [Fact]
    public void AFailedLoadIsRetriedOnTheNextApply()
    {
        _session.Throws = true;
        using var service = Service();
        var flows = new[] { On("f", "session.unlock") };

        service.Apply(flows);
        Assert.Equal(0, service.RunningSources);

        _session.Throws = false;
        service.Apply(flows);

        Assert.Equal(2, _session.Loads);
        Assert.Equal(1, service.RunningSources);
    }

    [Fact]
    public async Task AnEventThatStartedNothingDoesNotEatTheCooldown()
    {
        using var service = Service(new TriggerOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)));
        service.Apply([On("f", "power.battery", only: "onBattery")]);

        _onBattery.Value = false;
        _power.Raise("power.battery");
        await SettleAsync(service);
        Assert.Empty(_provider.Attempts);

        _onBattery.Value = true;
        _time.Advance(TimeSpan.FromSeconds(5)); // past the debounce, inside the cooldown a start would have set
        _power.Raise("power.battery");
        await _provider.Started("a");
        await SettleAsync(service);
        Assert.Single(_provider.Attempts);
    }

    [Fact]
    public async Task AnUnchangedReloadKeepsTheCooldown()
    {
        using var service = Service(new TriggerOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)));
        service.Apply([On("f", "session.unlock")]);
        _session.Raise("session.unlock");
        await _provider.Started("a");
        await SettleAsync(service);

        service.Apply([On("f", "session.unlock")]); // new Flow and trigger instances, equal by value
        _time.Advance(TimeSpan.FromSeconds(5));
        _session.Raise("session.unlock");
        await SettleAsync(service);

        Assert.Single(_provider.Attempts);
    }

    [Fact]
    public async Task AReplacedTriggerAtTheSameIndexStartsWithoutACooldown()
    {
        using var service = Service(new TriggerOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)));
        service.Apply([On("f", "session.unlock")]);
        _session.Raise("session.unlock");
        await _provider.Started("a");
        await SettleAsync(service);

        service.Apply([On("f", "session.lock")]); // the same flow and index, a different trigger
        _time.Advance(TimeSpan.FromSeconds(5));
        _session.Raise("session.lock");
        await _provider.Started("a");
        await SettleAsync(service);

        Assert.Equal(2, _provider.Attempts.Count);
    }

    [Fact]
    public async Task AnotherWifiDoesNotSwallowTheOneATriggerWaitsFor()
    {
        using var service = Service();
        service.Apply([On("home", "network.connected", ssid: "Home")]);

        _network.Raise("network.connected", detail: "Office", kind: "wifi");
        _network.Raise("network.connected", detail: "Home", kind: "wifi"); // inside the debounce of Office's event
        await _provider.Started("a");
        await SettleAsync(service);

        Assert.Single(_provider.Attempts);
    }

    [Fact]
    public async Task ViaNarrowsToWifiOrCableOnBothEvents()
    {
        using var service = Service();
        service.Apply([
            On("cable-in", "network.connected", via: NetworkVia.Cable),
            On("cable-out", "network.disconnected", via: NetworkVia.Cable),
            On("wifi-in", "network.connected", via: NetworkVia.WiFi),
            On("home-out", "network.disconnected", ssid: "Home"),
        ]);
        var started = new List<string>();
        var four = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _launcher.RunCompleted += r =>
        {
            lock (started)
            {
                started.Add(r.FlowId);
                if (started.Count == 4)
                    four.TrySetResult();
            }
        };

        _network.Raise("network.connected", detail: "Ethernet 4", kind: "cable");
        _network.Raise("network.disconnected", detail: "Home", kind: "wifi");
        _network.Raise("network.disconnected", detail: "Ethernet 4", kind: "cable");
        _network.Raise("network.connected", detail: "Office", kind: "wifi");
        _network.Raise("network.connected"); // a source that couldn't say what it was: no filter matches
        // IdleAsync only covers the hand-over to the launcher; the runs finish on their own (F-042: a slow CI box
        // missed one inside SettleAsync's 50 ms), so wait for the four, then settle to catch an extra one.
        await four.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await SettleAsync(service);

        lock (started)
            Assert.Equal(["cable-in", "cable-out", "home-out", "wifi-in"], started.Order());
    }

    [Fact]
    public async Task AnotherConnectionIsNotHeldBackByTheCooldown()
    {
        using var service = Service();
        service.Apply([On("any", "network.connected")]);
        var started = 0;
        using var completed = new SemaphoreSlim(0);
        _launcher.RunCompleted += _ => { Interlocked.Increment(ref started); completed.Release(); };

        _network.Raise("network.connected", detail: "Home", kind: "wifi");
        await completed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(3)); // past the debounce, inside the cooldown
        _network.Raise("network.connected", detail: "Home", kind: "wifi"); // the same connection again: held back
        await SettleAsync(service);
        _network.Raise("network.connected", detail: "Ethernet 4", kind: "cable"); // another one: starts
        await completed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(3));
        _network.Raise("network.connected", detail: "Home", kind: "cable"); // an adapter named like the Wi-Fi is still another one
        await completed.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(3, Volatile.Read(ref started));
    }

    [Fact]
    public async Task ACooldownHoldsTheTriggerBack()
    {
        using var service = Service(new TriggerOptions(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5)));
        service.Apply([On("f", "session.unlock")]);

        _session.Raise("session.unlock");
        await _provider.Started("a");
        _time.Advance(TimeSpan.FromSeconds(10)); // past the debounce, inside the cooldown
        _session.Raise("session.unlock");
        await SettleAsync(service);
        Assert.Single(_provider.Attempts);

        _time.Advance(TimeSpan.FromSeconds(25));
        _session.Raise("session.unlock");
        await _provider.Started("a");
        await SettleAsync(service);
        Assert.Equal(2, _provider.Attempts.Count);
    }

    [Fact]
    public async Task EventsWhilePausedAreDroppedNotQueued()
    {
        using var service = Service();
        service.Apply([On("f", "session.unlock")]);
        _paused = true;

        _session.Raise("session.unlock");
        await SettleAsync(service);
        _paused = false;
        await SettleAsync(service);

        Assert.Empty(_provider.Attempts);
    }

    [Fact]
    public async Task OnlyDecidesWhetherItStarts()
    {
        using var service = Service(new TriggerOptions(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(5)));
        service.Apply([On("f", "session.unlock", only: "onBattery")]);

        _onBattery.Value = false;
        _session.Raise("session.unlock");
        await SettleAsync(service);
        Assert.Empty(_provider.Attempts);
        Assert.Contains(_log, l => l.Contains("onBattery isn't true", StringComparison.Ordinal));

        _onBattery.Value = true;
        _session.Raise("session.unlock");
        await _provider.Started("a");
        Assert.Single(_provider.Attempts);
    }

    [Fact]
    public async Task AnUnreadableConditionStartsAnyway()
    {
        using var service = Service();
        service.Apply([On("f", "session.unlock", only: "onBattery")]);
        _onBattery.Unknown = true;

        _session.Raise("session.unlock");
        await _provider.Started("a");

        Assert.Contains(_log, l => l.Contains("starting anyway", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnknownConditionSkipsTheTrigger()
    {
        using var service = Service();

        service.Apply([On("f", "session.unlock", only: "onFire")]);
        _session.Raise("session.unlock");
        await SettleAsync(service);

        Assert.False(_session.Loaded);
        Assert.Empty(_provider.Attempts);
        Assert.Contains(_log, l => l.Contains("onFire", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheChainGuardStopsALoop()
    {
        using var service = Service();
        service.Apply([On("f", "session.unlock")]);

        _session.Raise("session.unlock", chain: ["g", "f"]); // the event descends from a run of f itself
        await SettleAsync(service);

        Assert.Empty(_provider.Attempts);
        Assert.Contains(_log, l => l.Contains("loop", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhenRunningIsTheLaunchers()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _provider.On("a", (_, token) => gate.Task.WaitAsync(token));
        using var service = Service(new TriggerOptions(TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(5)));
        service.Apply([On("f", "session.unlock", whenRunning: WhenRunning.Ignore)]);

        _session.Raise("session.unlock");
        await _provider.Started("a");
        _session.Raise("session.unlock"); // the flow is still running: ignore drops it
        await SettleAsync(service);
        gate.SetResult();

        Assert.Single(_provider.Attempts);
    }

    /// <summary>Everything started so far has run to its end: no fixed delay, so "did not start" is exact (history is written before a run ends).</summary>
    private async Task<List<string>> StartedAsync(TriggerService service)
    {
        await service.IdleAsync();
        var give = System.Diagnostics.Stopwatch.StartNew();
        while (_known.Keys.Any(_launcher.IsRunning))
        {
            Assert.True(give.Elapsed < TimeSpan.FromSeconds(10), "a run never finished");
            await Task.Yield();
        }
        return [.. _history.Recent.Select(r => r.FlowId)];
    }

    [Fact]
    public async Task ADeviceFilterWantsItsTextInTheNameAnyCase()
    {
        using var service = Service();
        service.Apply([On("xbox", "device.added", device: "xbox"), On("any", "device.added")]);

        _devices.Raise("device.added", detail: "USB Keyboard");
        Assert.Equal(["any"], await StartedAsync(service));

        _time.Advance(TimeSpan.FromMinutes(1));
        _devices.Raise("device.added"); // a name that couldn't be read matches no filter
        await StartedAsync(service); // the flow ignores a start while it runs (whenRunning), so each event waits for the last run
        _time.Advance(TimeSpan.FromMinutes(1));
        _devices.Raise("device.added", detail: "Xbox Wireless Controller");
        var started = await StartedAsync(service);

        Assert.Equal(1, started.Count(id => id == "xbox"));
        Assert.Equal(3, started.Count(id => id == "any"));
    }

    [Fact]
    public async Task SeveralArrivalsOfOneDeviceFoldIntoOneStartButAnotherDeviceStarts()
    {
        using var service = Service();
        service.Apply([On("f", "device.added")]);

        _devices.Raise("device.added", detail: "Xbox Wireless Controller");
        _devices.Raise("device.added", detail: "Xbox Wireless Controller");
        _devices.Raise("device.added", detail: "Xbox Wireless Controller");
        Assert.Single(await StartedAsync(service));

        _devices.Raise("device.added", detail: "USB Keyboard");
        Assert.Equal(2, (await StartedAsync(service)).Count);
    }

    [Fact]
    public async Task ARemovalFilterAndTheEventsDetailReachTheRun()
    {
        using var service = Service();
        service.Apply([On("f", "device.removed", device: "xbox")]);

        _devices.Raise("device.added", detail: "Xbox Wireless Controller"); // the other event: not subscribed
        _devices.Raise("device.removed", detail: "Xbox Wireless Controller");

        Assert.Equal(["f"], await StartedAsync(service));
    }

    [Fact]
    public async Task TheDefaultSoundDeviceEventWantsTheLibraryItemsDeviceAndFlow()
    {
        _library = Library.Empty with { Audio = [new AudioDevice("headphones", "Headphones"), new AudioDevice("mic", "Headphones", AudioFlow.Input)] };
        using var service = Service();
        service.Apply([On("hp", "audio.default-changed", device: "headphones"), On("any", "audio.default-changed")]);

        _devices.Raise("audio.default-changed", detail: "Speakers (Realtek)", kind: "output");
        Assert.Equal(["any"], await StartedAsync(service));

        _time.Advance(TimeSpan.FromMinutes(1));
        _devices.Raise("audio.default-changed", detail: "Headphones (WH-1000XM5)", kind: "input"); // the microphone isn't the output
        await StartedAsync(service);
        _time.Advance(TimeSpan.FromMinutes(1));
        _devices.Raise("audio.default-changed", detail: "Headphones (WH-1000XM5)", kind: "output");
        var started = await StartedAsync(service);

        Assert.Equal(1, started.Count(id => id == "hp"));
        Assert.Equal(3, started.Count(id => id == "any"));
    }

    [Fact]
    public async Task ADeviceFilterWithoutAKnownNameStartsNothingAndKeepsTheCooldown()
    {
        _library = Library.Empty with { Audio = [new AudioDevice("headphones", "Headphones")] };
        using var service = Service();
        service.Apply([On("hp", "audio.default-changed", device: "headphones")]);

        _devices.Raise("audio.default-changed", kind: "output");
        Assert.Empty(await StartedAsync(service));

        _time.Advance(TimeSpan.FromMinutes(1));
        _devices.Raise("audio.default-changed", detail: "Headphones", kind: "output");
        Assert.Equal(["hp"], await StartedAsync(service));
    }

    [Fact]
    public async Task DisposeUnloadsEverythingAndIgnoresLateEvents()
    {
        var service = Service();
        service.Apply([On("f", "session.unlock")]);

        service.Dispose();
        _session.Raise("session.unlock");
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.False(_session.Loaded);
        Assert.Empty(_provider.Attempts);
    }
}
