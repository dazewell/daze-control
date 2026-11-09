namespace DazeControl.Runtime.Triggers;

/// <summary>One live network connection: a Wi-Fi network or a cable adapter that is up with a gateway.</summary>
/// <param name="Id">Stable while the connection lasts: "wifi:" + the network name, "cable:" + the adapter id.</param>
/// <param name="Kind">"wifi" or "cable" (<c>TriggerText.ViaText</c>).</param>
/// <param name="Detail">What a person calls it: the Wi-Fi name, or the adapter's name ("Ethernet 2").</param>
/// <param name="Adapter">The adapter it is on, when a Wi-Fi name may be unreadable for a moment.</param>
/// <param name="Named">False when the Wi-Fi name couldn't be read (the adapter stands for it): the same adapter with or
/// without its name is the same connection, so a passing WLAN read failure raises nothing.</param>
public sealed record NetworkConnection(string Id, string Kind, string Detail, string? Adapter = null, bool Named = true);

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
        // Read, commit and raise under one lock: two notifications (interface and route) can't commit or deliver out
        // of order. Raising here is safe: the trigger service never calls back into the tracker.
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
            var gone = was.Values.Where(c => !now.ContainsKey(c.Id)).ToList();
            var came = now.Values.Where(c => !was.ContainsKey(c.Id)).ToList();
            foreach (var c in came.ToList())
            {
                // The same adapter with its Wi-Fi name and without it: one connection. Keep the named one, so the name
                // coming back later matches again.
                if (c.Adapter is null || gone.FirstOrDefault(g => g.Adapter == c.Adapter && (!g.Named || !c.Named)) is not { } same)
                    continue;
                gone.Remove(same);
                came.Remove(c);
                if (!c.Named)
                {
                    now.Remove(c.Id);
                    now[same.Id] = same;
                }
            }
            // Gone first: a Wi-Fi switch reads "Home disconnected, Office connected".
            foreach (var g in gone)
                raise(new TriggerEvent("network.disconnected", Detail: g.Detail, Kind: g.Kind));
            foreach (var c in came)
                raise(new TriggerEvent("network.connected", Detail: c.Detail, Kind: c.Kind));
        }
    }
}
