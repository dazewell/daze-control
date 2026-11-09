using DazeControl.Runtime.Triggers;

namespace DazeControl.Runtime.Tests;

/// <summary>T20.4: network.connected/disconnected comes from a tracker that turns "something changed" into real changes.</summary>
public sealed class NetworkTrackerTests
{
    private (bool Connected, string? Ssid) _state;
    private readonly List<TriggerEvent> _events = [];
    private readonly NetworkTracker _tracker;

    public NetworkTrackerTests() => _tracker = new NetworkTracker(() => _state, _events.Add);

    private void Now(bool connected, string? ssid = null)
    {
        _state = (connected, ssid);
        _tracker.Changed();
    }

    [Fact]
    public void TheFirstReadingIsABaseline()
    {
        Now(true, "Home");
        Assert.Empty(_events);
    }

    [Fact]
    public void LosingAndRegainingTheNetworkRaisesBoth()
    {
        Now(true, "Home");
        Now(false);
        Now(true, "Home");

        Assert.Equal(["network.disconnected", "network.connected"], _events.Select(e => e.Name));
        Assert.Equal("Home", _events[1].Detail);
    }

    [Fact]
    public void RepeatedNotificationsOfTheSameStateRaiseNothing()
    {
        Now(false);
        Now(false);
        Now(true);
        Now(true);
        Assert.Equal(["network.connected"], _events.Select(e => e.Name));
    }

    [Fact]
    public void SwitchingWifiCountsAsConnecting()
    {
        Now(true, "Home");
        Now(true, "Office");
        Assert.Equal("Office", Assert.Single(_events).Detail);
    }

    [Fact]
    public void AnUnreadableStateSaysNothingAndResetStartsOver()
    {
        var failing = new NetworkTracker(() => throw new InvalidOperationException("no"), _events.Add);
        failing.Changed();
        Assert.Empty(_events);

        Now(true);
        _tracker.Reset();
        Now(false); // a new baseline
        Assert.Empty(_events);
    }
}
