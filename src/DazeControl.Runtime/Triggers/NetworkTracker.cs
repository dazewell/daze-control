namespace DazeControl.Runtime.Triggers;

/// <summary>
/// The logic of <c>network.connected/disconnected</c> (T20.4): the OS says "connectivity changed" (often several times
/// for one change, and with no word on what it was), this keeps the last known state and raises only a real change.
/// The first reading after <see cref="Reset"/> is the baseline, not an event. Reading the state and the Wi-Fi name is
/// the caller's job (<paramref name="read"/>), so tests drive it with a fake.
/// </summary>
public sealed class NetworkTracker(Func<(bool Connected, string? Ssid)> read, Action<TriggerEvent> raise)
{
    private readonly Lock _lock = new();
    private bool? _connected;
    private string? _ssid;

    /// <summary>Forgets the state: the next <see cref="Changed"/> is a baseline (called on Load).</summary>
    public void Reset()
    {
        lock (_lock)
            (_connected, _ssid) = (null, null);
    }

    /// <summary>Called on every OS notification (any thread).</summary>
    public void Changed()
    {
        TriggerEvent? e2 = null;
        // The read happens under the lock, so concurrent notifications (and Reset) can't commit out of order.
        lock (_lock)
        {
            (bool Connected, string? Ssid) now;
            try
            {
                now = read();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                return; // unknown: say nothing rather than guess
            }
            var was = _connected;
            var wasSsid = _ssid;
            (_connected, _ssid) = (now.Connected, now.Connected ? now.Ssid : null);
            if (was is null)
                return;
            if (now.Connected && (!was.Value || !string.Equals(wasSsid, _ssid, StringComparison.Ordinal)))
                e2 = new TriggerEvent("network.connected", Detail: _ssid); // a new Wi-Fi network counts as a new connection
            else if (!now.Connected && was.Value)
                e2 = new TriggerEvent("network.disconnected");
        }
        if (e2 is not null)
            raise(e2);
    }
}
