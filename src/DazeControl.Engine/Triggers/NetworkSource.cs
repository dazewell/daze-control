using System.Net.NetworkInformation;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DazeControl.Runtime.Triggers;

namespace DazeControl.Engine.Triggers;

/// <summary>
/// <c>network.connected/disconnected</c> (M20, T20.4): <c>NotifyNetworkConnectivityChange</c> (iphlpapi), registered
/// only while loaded. The callback only says "something changed"; <see cref="NetworkTracker"/> compares with the last
/// state and raises a real change, with the Wi-Fi name (read from the WLAN API at that moment, no resident handle)
/// so <c>ssid:</c> can narrow a trigger. No polling, no admin.
/// </summary>
internal sealed unsafe partial class NetworkSource : ITriggerSource
{
    private readonly NetworkTracker _tracker;
    private readonly Lock _lock = new();
    private GCHandle _self;
    private nint _registration;

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
            var result = NotifyNetworkConnectivityChange(0 /* AF_UNSPEC */, &OnChanged, (void*)GCHandle.ToIntPtr(_self), 0, &handle);
            if (result != 0)
            {
                _self.Free();
                throw new InvalidOperationException($"NotifyNetworkConnectivityChange failed: {result}");
            }
            _registration = handle;
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
            _registration = 0;
            _self.Free();
        }
    }

    public void Dispose() => Unload();

    private static (bool Connected, string? Ssid) Read()
    {
        var connected = NetworkInterface.GetIsNetworkAvailable();
        return (connected, connected ? CurrentSsid() : null);
    }

    private static string? CurrentSsid()
    {
        nint client = 0;
        try
        {
            if (WlanOpenHandle(2, 0, out _, out client) != 0 || WlanEnumInterfaces(client, 0, out var list) != 0)
                return null;
            try
            {
                var count = *(uint*)list;
                for (var i = 0u; i < count; i++)
                {
                    // WLAN_INTERFACE_INFO_LIST: dwNumberOfItems, dwIndex, then 532-byte items: GUID, description[256], state.
                    var item = (byte*)list + 8 + i * 532;
                    if (WlanQueryInterface(client, (Guid*)item, 7 /* current_connection */, 0, out _, out var data, 0) != 0)
                        continue;
                    try
                    {
                        // WLAN_CONNECTION_ATTRIBUTES: state, mode, profile[256], then the association's DOT11_SSID.
                        var length = *(uint*)((byte*)data + 520);
                        if (length is > 0 and <= 32)
                            return System.Text.Encoding.UTF8.GetString((byte*)data + 524, (int)length);
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
            // No WLAN service or adapter: no name.
        }
        finally
        {
            if (client != 0)
                _ = WlanCloseHandle(client, 0);
        }
        return null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnChanged(void* context, int connectivityLevel)
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
    private static partial uint NotifyNetworkConnectivityChange(ushort family,
        delegate* unmanaged[Stdcall]<void*, int, void> callback, void* context, byte initialNotification, nint* handle);

    [LibraryImport("iphlpapi.dll")]
    private static partial uint CancelMibChangeNotify2(nint handle);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanOpenHandle(uint clientVersion, nint reserved, out uint negotiated, out nint client);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanCloseHandle(nint client, nint reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanEnumInterfaces(nint client, nint reserved, out nint list);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanQueryInterface(nint client, Guid* interfaceGuid, int opcode, nint reserved, out uint size, out nint data, int type);

    [LibraryImport("wlanapi.dll")]
    private static partial void WlanFreeMemory(nint memory);
}
