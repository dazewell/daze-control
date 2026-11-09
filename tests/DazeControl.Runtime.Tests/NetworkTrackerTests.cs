using DazeControl.Runtime.Triggers;

namespace DazeControl.Runtime.Tests;

/// <summary>T20.4/T20.12: network.connected/disconnected per connection, from a tracker that turns "something changed" into real changes.</summary>
public sealed class NetworkTrackerTests
{
    private static readonly NetworkConnection Home = new("wifi:Home", "wifi", "Home");
    private static readonly NetworkConnection Office = new("wifi:Office", "wifi", "Office");
    private static readonly NetworkConnection Cable = new("cable:{1}", "cable", "Ethernet 4");

    private NetworkConnection[] _state = [];
    private readonly List<TriggerEvent> _events = [];
    private readonly NetworkTracker _tracker;

    public NetworkTrackerTests() => _tracker = new NetworkTracker(() => _state, _events.Add);

    private void Now(params NetworkConnection[] connections)
    {
        _state = connections;
        _tracker.Changed();
    }

    private IEnumerable<string> Said => _events.Select(e => $"{e.Name} {e.Kind} {e.Detail}");

    [Fact]
    public void TheFirstReadingIsABaseline()
    {
        Now(Home);
        Assert.Empty(_events);
    }

    [Fact]
    public void ACableComesAndGoesWhileWifiStaysUp()
    {
        Now(); // offline
        Now(Home); // Wi-Fi up
        Now(Home, Cable); // cable plugged in while Wi-Fi is up
        Now(Home); // cable out, Wi-Fi stays
        Now(); // Wi-Fi down

        Assert.Equal([
            "network.connected wifi Home",
            "network.connected cable Ethernet 4",
            "network.disconnected cable Ethernet 4",
            "network.disconnected wifi Home",
        ], Said);
    }

    [Fact]
    public void BothComingUpAtOnceAreTwoConnections()
    {
        Now();
        Now(Cable, Home);
        Assert.Equal(["network.connected cable Ethernet 4", "network.connected wifi Home"], Said);
    }

    [Fact]
    public void FlappingRaisesEveryRealChangeAndRepeatsNothing()
    {
        Now(Home);
        Now(Home, Cable);
        Now(Home, Cable); // the OS repeats itself
        Now(Home);
        Now(Home, Cable);
        Now(Home);

        Assert.Equal([
            "network.connected cable Ethernet 4",
            "network.disconnected cable Ethernet 4",
            "network.connected cable Ethernet 4",
            "network.disconnected cable Ethernet 4",
        ], Said);
    }

    [Fact]
    public void SwitchingWifiIsOneGoingAndOneComing()
    {
        Now(Home);
        Now(Office);
        Assert.Equal(["network.disconnected wifi Home", "network.connected wifi Office"], Said);
    }

    [Fact]
    public void AWifiNameUnreadableForAMomentIsTheSameConnection()
    {
        var home = Home with { Adapter = "{w}" };
        var unnamed = new NetworkConnection("wifi:{w}", "wifi", "Wi-Fi", "{w}", Named: false);
        Now(home);
        Now(home, Cable);
        Now(unnamed, Cable); // WLAN couldn't say the name
        Now(home, Cable); // and now it can again
        Now(unnamed);
        Now();

        Assert.Equal([
            "network.connected cable Ethernet 4",
            "network.disconnected cable Ethernet 4",
            "network.disconnected wifi Home",
        ], Said);
    }

    [Fact]
    public void EventsAreDeliveredInTheOrderTheyWereSeen()
    {
        // A notification arriving while another delivers waits for it (one lock), so a connect can't land after its disconnect.
        var gate = new ManualResetEventSlim();
        var raised = new List<string>();
        var state = new[] { Cable };
        var first = true;
        NetworkTracker tracker = null!;
        tracker = new NetworkTracker(() => state, e =>
        {
            lock (raised)
                raised.Add(e.Name);
            if (first)
            {
                first = false;
                state = [];
                var second = Task.Run(tracker.Changed);
                Assert.False(second.Wait(100, TestContext.Current.CancellationToken)); // blocked until this delivery ends
                gate.Set();
            }
        });
        state = [];
        tracker.Changed();
        state = [Cable];
        tracker.Changed();
        gate.Wait(TestContext.Current.CancellationToken);
        SpinWait.SpinUntil(() => { lock (raised) return raised.Count == 2; }, 2000);

        lock (raised)
            Assert.Equal(["network.connected", "network.disconnected"], raised);
    }

    [Fact]
    public void AnUnreadableStateSaysNothingAndResetStartsOver()
    {
        var failing = new NetworkTracker(() => throw new InvalidOperationException("no"), _events.Add);
        failing.Changed();
        Assert.Empty(_events);

        Now(Home);
        _tracker.Reset();
        Now(); // a new baseline
        Assert.Empty(_events);
    }
}
