using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using CtrlAltDaze.Runtime.Triggers;

namespace CtrlAltDaze.Providers.Network;

/// <summary>An adapter that is up, for the VPN check: its name, the driver's description and its type.</summary>
public readonly record struct NetworkAdapter(string Name, string Description, NetworkInterfaceType Type);

/// <summary>What Windows' own connectivity check last found (NLM, no traffic sent by us): internet or not, metered or not.</summary>
public readonly record struct Connectivity(bool Internet, bool Captive, bool Metered);

/// <summary>The network as it is now, behind a seam (D-033). One-shot reads; nothing held between calls (idle = zero).</summary>
public interface INetworkState
{
    /// <summary>Wi-Fi and cable connections: adapters up with a gateway (the same rule as the network trigger, D-107).</summary>
    IReadOnlyList<NetworkConnection> Connections();

    /// <summary>Adapters that are up, loopback excluded.</summary>
    IReadOnlyList<NetworkAdapter> Adapters();

    /// <summary>Windows' connectivity hint; null when Windows doesn't know.</summary>
    Connectivity? Connectivity();
}

/// <summary>
/// Reads the network on demand. A connection = an adapter that is up and has a gateway: Wi-Fi (named by its network,
/// read from the WLAN API at that moment, no resident handle) or an Ethernet-type adapter ("cable"). Loopback, tunnels
/// and adapters without a gateway (filter drivers, WAN miniports, Hyper-V/WSL internal switches) are not connections.
/// Shared by the network trigger (<c>NetworkSource</c>) and the network checks (D-118).
/// </summary>
public sealed unsafe partial class WindowsNetworkState : INetworkState
{
    public IReadOnlyList<NetworkConnection> Connections() => Read();

    public IReadOnlyList<NetworkAdapter> Adapters()
    {
        List<NetworkAdapter> adapters = [];
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus == OperationalStatus.Up && adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                adapters.Add(new NetworkAdapter(adapter.Name, adapter.Description, adapter.NetworkInterfaceType));
        }
        return adapters;
    }

    public Connectivity? Connectivity()
    {
        // GetNetworkConnectivityHint (Windows 10 2004+): the cached result of Windows' own probe, no request from us.
        if (GetNetworkConnectivityHint(out var hint) != 0)
            return null;
        return hint.Level switch
        {
            1 or 2 => new Connectivity(false, false, Metered(hint)), // none, local only
            3 => new Connectivity(true, false, Metered(hint)),
            4 => new Connectivity(false, true, Metered(hint)), // a sign-in page (captive portal)
            _ => null, // unknown, hidden
        };
    }

    private static bool Metered(ConnectivityHint hint) => hint.Cost is 2 or 3 || hint.OverDataLimit != 0 || hint.Roaming != 0;

    /// <summary>The live connections (Wi-Fi by network name, cable by adapter id).</summary>
    public static List<NetworkConnection> Read()
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
                ? new NetworkConnection("wifi:" + name, "wifi", name, adapter.Id)
                : new NetworkConnection("wifi:" + adapter.Id, "wifi", adapter.Name, adapter.Id, Named: false));
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

    /// <summary>Wi-Fi network names for the check's suggestions: the connected ones first, then the saved ones.</summary>
    public static List<(string Name, bool Connected)> WifiNetworks()
    {
        List<(string Name, bool Connected)> networks = [];
        foreach (var connection in Read())
        {
            if (connection.Kind == "wifi" && connection.Named)
                networks.Add((connection.Detail, true));
        }
        nint client = 0;
        try
        {
            if (WlanOpenHandle(2, 0, out _, out client) != 0 || WlanEnumInterfaces(client, 0, out var list) != 0)
                return networks;
            try
            {
                var count = *(uint*)list;
                for (var i = 0u; i < count; i++)
                {
                    var item = (byte*)list + 8 + i * 532;
                    if (WlanGetProfileList(client, (Guid*)item, 0, out var profiles) != 0)
                        continue;
                    try
                    {
                        // WLAN_PROFILE_INFO_LIST: dwNumberOfItems, dwIndex, then 516-byte items: name[256 WCHAR], flags.
                        var profileCount = *(uint*)profiles;
                        for (var j = 0u; j < profileCount; j++)
                        {
                            var name = new string((char*)((byte*)profiles + 8 + j * 516));
                            if (name.Length > 0 && !networks.Any(n => string.Equals(n.Name, name, StringComparison.OrdinalIgnoreCase)))
                                networks.Add((name, false));
                        }
                    }
                    finally
                    {
                        WlanFreeMemory(profiles);
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
            // No WLAN service: what is connected is all there is.
        }
        finally
        {
            if (client != 0)
                _ = WlanCloseHandle(client, 0);
        }
        return networks;
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

    /// <summary>
    /// NL_NETWORK_CONNECTIVITY_HINT (nldef.h): two enums, then three 1-byte BOOLEANs (not 4-byte BOOL), 12 bytes in all.
    /// The level enum starts at Unknown = 0, so internet access is 3; both checked on a live PC (2026-11-11).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct ConnectivityHint
    {
        public int Level, Cost;
        public byte ApproachingDataLimit, OverDataLimit, Roaming;
    }

    [LibraryImport("iphlpapi.dll")]
    private static partial int GetNetworkConnectivityHint(out ConnectivityHint hint);

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
    private static partial uint WlanGetProfileList(nint client, Guid* interfaceGuid, nint reserved, out nint list);

    [LibraryImport("wlanapi.dll")]
    private static partial void WlanFreeMemory(nint memory);
}
