namespace DazeControl.Runtime.Triggers;

/// <summary>One live network connection: a Wi-Fi network or a cable adapter that is up with a gateway.</summary>
/// <param name="Id">Stable while the connection lasts: "wifi:" + the network name, "cable:" + the adapter id.</param>
/// <param name="Kind">"wifi" or "cable" (<c>TriggerText.ViaText</c>).</param>
/// <param name="Detail">What a person calls it: the Wi-Fi name, or the adapter's name ("Ethernet 2").</param>
public sealed record NetworkConnection(string Id, string Kind, string Detail);

/// <summary>
/// The logic of <c>network.connected/disconnected</c> (T20.4, per connection since T20.12): the OS says "something
/// changed" (often several times for one change, and with no word on what it was), this keeps the last set of
/// connections and raises one event per connection that came or went, so a cable plugged in while Wi-Fi stays up is
/// a <c>network.connected</c> of its own. The first reading after <see cref="Reset"/> is the baseline, not an event.
/// Reading the connections is the caller's job (<paramref name="read"/>), so tests drive it with a fake.
/// </summary>
public sealed class NetworkTracker(Func<IReadOnlyCollection<NetworkConnection>> read, Action<TriggerEvent> raise)
{
    private readonly Lock _lock = new();
    private Dictionary<string, NetworkConnection>? _connections;

    /// <summary>Forgets the state: the next <see cref="Changed"/> is a baseline (called on Load).</summary>
    public void Reset()
    {
        lock (_lock)
            _connections = null;
    }

    /// <summary>Called on every OS notification (any thread).</summary>
    public void Changed()
    {
        List<TriggerEvent> events = [];
        // The read happens under the lock, so concurrent notifications (and Reset) can't commit out of order.
        lock (_lock)
        {
            Dictionary<string, NetworkConnection> now = [];
            try
            {
                foreach (var connection in read())
                    now.TryAdd(connection.Id, connection);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return; // unknown: say nothing rather than guess
            }
            var was = _connections;
            _connections = now;
            if (was is null)
                return;
            // Gone first: a Wi-Fi switch reads "Home disconnected, Office connected".
            foreach (var gone in was.Values.Where(c => !now.ContainsKey(c.Id)))
                events.Add(new TriggerEvent("network.disconnected", Detail: gone.Detail, Kind: gone.Kind));
            foreach (var came in now.Values.Where(c => !was.ContainsKey(c.Id)))
                events.Add(new TriggerEvent("network.connected", Detail: came.Detail, Kind: came.Kind));
        }
        foreach (var e in events)
            raise(e);
    }
}
