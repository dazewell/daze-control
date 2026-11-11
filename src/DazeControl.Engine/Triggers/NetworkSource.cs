using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DazeControl.Providers.Network;
using DazeControl.Runtime.Triggers;

namespace DazeControl.Engine.Triggers;

/// <summary>
/// <c>network.connected/disconnected</c> (M20, T20.4; per connection since T20.12): <c>NotifyIpInterfaceChange</c> (an
/// adapter came, went or changed) and <c>NotifyRouteChange2</c> (a gateway came or went: DHCP often answers after the
/// link is up), registered only while loaded. A callback only says "something changed"; <see cref="NetworkTracker"/>
/// compares the live connections with the last ones. A connection = an adapter that is up and has a gateway: Wi-Fi
/// (named by its network, read from the WLAN API at that moment, no resident handle) or an Ethernet-type adapter
/// ("cable"). Loopback, tunnels and adapters without a gateway (filter drivers, WAN miniports, Hyper-V/WSL internal
/// switches) are not connections. No polling, no admin.
/// </summary>
internal sealed unsafe partial class NetworkSource : ITriggerSource
{
    private readonly NetworkTracker _tracker;
    private readonly Lock _lock = new();
    private GCHandle _self;
    private nint _registration;
    private nint _routes;

    public NetworkSource() => _tracker = new NetworkTracker(WindowsNetworkState.Read, e => Raised?.Invoke(e));

    public IReadOnlyList<string> Events { get; } = ["network.connected", "network.disconnected"];

    public event Action<TriggerEvent>? Raised;

    public void Load()
    {
        lock (_lock)
        {
            if (_self.IsAllocated)
                return;
            _self = GCHandle.Alloc(this);
            nint handle;
            var result = NotifyIpInterfaceChange(0 /* AF_UNSPEC */, &OnChanged, (void*)GCHandle.ToIntPtr(_self), 0, &handle);
            if (result != 0)
            {
                _self.Free();
                throw new InvalidOperationException($"NotifyIpInterfaceChange failed: {result}");
            }
            _registration = handle;
            nint routes;
            result = NotifyRouteChange2(0 /* AF_UNSPEC */, &OnChanged, (void*)GCHandle.ToIntPtr(_self), 0, &routes);
            if (result != 0)
            {
                _ = CancelMibChangeNotify2(_registration);
                _registration = 0;
                _self.Free();
                throw new InvalidOperationException($"NotifyRouteChange2 failed: {result}");
            }
            _routes = routes;
            _tracker.Reset();
            _tracker.Changed(); // the baseline, not an event
        }
    }

    public void Unload()
    {
        lock (_lock)
        {
            if (!_self.IsAllocated)
                return;
            _ = CancelMibChangeNotify2(_registration); // waits for a callback in flight
            _ = CancelMibChangeNotify2(_routes);
            (_registration, _routes) = (0, 0);
            _self.Free();
        }
    }

    public void Dispose() => Unload();

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnChanged(void* context, void* row, int notificationType)
    {
        try
        {
            if (GCHandle.FromIntPtr((nint)context).Target is NetworkSource self)
                self._tracker.Changed();
        }
        catch (Exception)
        {
            // Never unwind into the network stack.
        }
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial uint NotifyIpInterfaceChange(ushort family,
        delegate* unmanaged[Stdcall]<void*, void*, int, void> callback, void* context, byte initialNotification, nint* handle);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint NotifyRouteChange2(ushort family,
        delegate* unmanaged[Stdcall]<void*, void*, int, void> callback, void* context, byte initialNotification, nint* handle);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint CancelMibChangeNotify2(nint handle);
}
