using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
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

    public NetworkSource() => _tracker = new NetworkTracker(Read, e => Raised?.Invoke(e));

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

    private static List<NetworkConnection> Read()
    {
        List<NetworkConnection> connections = [];
        List<NetworkInterface>? wifi = null;
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up || !HasGateway(adapter))
                continue;
            switch (adapter.NetworkInterfaceType)
            {
                case NetworkInterfaceType.Wireless80211:
                    (wifi ??= []).Add(adapter);
                    break;
                case NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                    or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit:
                    connections.Add(new NetworkConnection("cable:" + adapter.Id, "cable", adapter.Name));
                    break;
            }
        }
        if (wifi is null)
            return connections; // no Wi-Fi up: don't touch the WLAN service at all
        var names = WifiNames();
        foreach (var adapter in wifi)
        {
            // A name the WLAN API couldn't give: the adapter stands for it, so the connection still counts.
            var name = Guid.TryParse(adapter.Id, out var id) && names.TryGetValue(id, out var ssid) ? ssid : null;
            connections.Add(name is not null
                ? new NetworkConnection("wifi:" + name, "wifi", name)
                : new NetworkConnection("wifi:" + adapter.Id, "wifi", adapter.Name));
        }
        return connections;
    }

    private static bool HasGateway(NetworkInterface adapter)
    {
        try
        {
            foreach (var gateway in adapter.GetIPProperties().GatewayAddresses)
            {
                var address = gateway.Address;
                if (!address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any))
                    return true;
            }
        }
        catch (NetworkInformationException)
        {
            // The adapter went away while being read: not a connection.
        }
        return false;
    }

    /// <summary>The network name of each connected Wi-Fi adapter, by its interface GUID.</summary>
    private static Dictionary<Guid, string> WifiNames()
    {
        Dictionary<Guid, string> names = [];
        nint client = 0;
        try
        {
            if (WlanOpenHandle(2, 0, out _, out client) != 0 || WlanEnumInterfaces(client, 0, out var list) != 0)
                return names;
            try
            {
                var count = *(uint*)list;
                for (var i = 0u; i < count; i++)
                {
                    // WLAN_INTERFACE_INFO_LIST: dwNumberOfItems, dwIndex, then 532-byte items: GUID, description[256], state.
                    var item = (byte*)list + 8 + i * 532;
                    if (WlanQueryInterface(client, (Guid*)item, 7 /* wlan_intf_opcode_current_connection */, 0, out var size, out var data, out _) != 0)
                        continue;
                    try
                    {
                        // WLAN_CONNECTION_ATTRIBUTES: state, mode, profile[256], then the association's DOT11_SSID.
                        var length = size >= 556 ? *(uint*)((byte*)data + 520) : 0; // 524 + the 32-byte SSID
                        if (length is > 0 and <= 32)
                            names[*(Guid*)item] = System.Text.Encoding.UTF8.GetString((byte*)data + 524, (int)length);
                    }
                    finally
                    {
                        WlanFreeMemory(data);
                    }
                }
            }
            finally
            {
                WlanFreeMemory(list);
            }
        }
        catch (Exception)
        {
            // No WLAN service or adapter: no names.
        }
        finally
        {
            if (client != 0)
                _ = WlanCloseHandle(client, 0);
        }
        return names;
    }

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

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiated, out nint client);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanCloseHandle(nint client, nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanEnumInterfaces(nint client, nint reserved, out nint list);

    // The last parameter is a pointer (PWLAN_OPCODE_VALUE_TYPE*): declared as an int, the 64-bit slot
    // held garbage in its upper half and wlanapi wrote through it (the engine crashed with an
    // access violation in wlanapi.dll on the first network change while on Wi-Fi, 2026-11-08).
    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanQueryInterface(nint client, Guid* interfaceGuid, int opcode, nint reserved, out uint size, out nint data, out int valueType);

    [LibraryImport("wlanapi.dll")]
    private static partial void WlanFreeMemory(nint memory);
}
