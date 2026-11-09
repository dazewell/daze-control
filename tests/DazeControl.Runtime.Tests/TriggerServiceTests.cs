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

        public void Raise(string name, IReadOnlyList<string>? chain = null, string? detail = null) => Raised?.Invoke(new TriggerEvent(name, chain, detail));

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
            ? new(flow, Library.Empty, flow.Name ?? flowId, null)
            : new(null, Library.Empty, flowId, "gone", CalledWhy.Missing);

        public bool IsRunning(string flowId) => owner._launcher.IsRunning(flowId);

        public StartAnswer Start(string flowId, string trigger, IReadOnlyList<string> chain, IReadOnlyDictionary<string, string> inputs, string callerName) =>
            throw new NotSupportedException();
    }

    private TriggerService Service(TriggerOptions? options = null) =>
        new([_session, _power, _network], _launcher, new FakeFlows(this), _time, () => _paused, [_onBattery], _log.Add, options);

    private Flow On(string id, string eventName, string? only = null, WhenRunning whenRunning = WhenRunning.Ignore, string? ssid = null)
    {
        var flow = Flow(Step("a")) with
        {
            Id = id,
            WhenRunning = whenRunning,
            Triggers = [new EventTrigger(eventName) { Only = only, Ssid = ssid }],
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

        _network.Raise("network.connected", detail: "Office");
        await SettleAsync(service);
        lock (started)
            Assert.Equal(["any"], started);

        _time.Advance(TimeSpan.FromMinutes(1));
        _network.Raise("network.connected", detail: "Home WiFi"); // case-insensitive
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

        _network.Raise("network.connected", detail: "Office");
        _network.Raise("network.connected", detail: "Home"); // inside the debounce of Office's event
        await _provider.Started("a");
        await SettleAsync(service);

        Assert.Single(_provider.Attempts);
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
