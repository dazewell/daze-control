using System.Runtime.InteropServices;
using DazeControl.Ipc;

namespace DazeControl.Providers.Bluetooth;

/// <summary>A paired device's node: "D9E08EC29685", "MX Master 3S"; Connected null when Windows doesn't say.</summary>
public sealed record BluetoothNode(string Address, string Name, bool? Connected);

/// <summary>Windows' paired Bluetooth devices, as Settings lists them (D-052).</summary>
public interface IPairedBluetooth
{
    IReadOnlyList<BluetoothNode> Present();
}

/// <summary>
/// Present <c>BTHENUM\DEV_&lt;addr&gt;</c> (classic) and <c>BTHLE\DEV_&lt;addr&gt;</c> (LE) device nodes through
/// CfgMgr32: no admin, no WinRT in the AOT engine (D-052). Unpaired devices lose their node, unlike their audio
/// endpoints (X-005).
/// </summary>
public sealed unsafe partial class CfgMgrPairedBluetooth : IPairedBluetooth
{
    private const uint FilterEnumerator = 0x1; // CM_GETIDLIST_FILTER_ENUMERATOR
    private const uint FilterPresent = 0x100; // CM_GETIDLIST_FILTER_PRESENT
    private const uint Success = 0; // CR_SUCCESS
    private const uint BufferSmall = 0x1A; // CR_BUFFER_SMALL
    private const uint TypeString = 0x12; // DEVPROP_TYPE_STRING
    private const uint TypeBoolean = 0x11; // DEVPROP_TYPE_BOOLEAN

    private static readonly DevPropKey FriendlyName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly DevPropKey DeviceDesc = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 2);

    // Undocumented: true while connected. Matched Settings' own AEP IsConnected on every paired device of the dev PC
    // (2026-11-02, 26300), audio or not.
    private static readonly DevPropKey IsConnected = new(new Guid("83da6326-97a6-4088-9453-a1923f573b29"), 15);

    public IReadOnlyList<BluetoothNode> Present()
    {
        var nodes = new List<BluetoothNode>();
        foreach (var enumerator in (string[])["BTHENUM", "BTHLE"])
        {
            foreach (var id in DeviceIds(enumerator))
            {
                if (AddressOf(id) is not { } address || CM_Locate_DevNodeW(out var node, id, 0) != Success)
                    continue;
                var name = StringProperty(node, FriendlyName) ?? StringProperty(node, DeviceDesc) ?? "";
                nodes.Add(new BluetoothNode(address, name, BooleanProperty(node, IsConnected)));
            }
        }
        return nodes;
    }

    /// <summary>"BTHLE\DEV_D9E08EC29685\7&amp;38A9E24E&amp;0&amp;D9E08EC29685" → "D9E08EC29685"; a service node → null.</summary>
    internal static string? AddressOf(string instanceId)
    {
        var slash = instanceId.IndexOf('\\', StringComparison.Ordinal);
        if (slash < 0 || !instanceId.AsSpan(slash + 1).StartsWith("DEV_", StringComparison.OrdinalIgnoreCase))
            return null;
        var start = slash + 5;
        if (instanceId.Length < start + 12)
            return null;
        var hex = instanceId.Substring(start, 12);
        return hex.All(char.IsAsciiHexDigit) && (instanceId.Length == start + 12 || instanceId[start + 12] == '\\')
            ? hex.ToUpperInvariant()
            : null;
    }

    private static List<string> DeviceIds(string enumerator)
    {
        // The list can grow between the two calls (a device pairs): ask again.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (CM_Get_Device_ID_List_SizeW(out var length, enumerator, FilterEnumerator | FilterPresent) != Success)
                return [];
            var buffer = new char[length];
            uint result;
            fixed (char* chars = buffer)
                result = CM_Get_Device_ID_ListW(enumerator, chars, length, FilterEnumerator | FilterPresent);
            if (result == BufferSmall)
                continue;
            if (result != Success)
                return [];
            return [.. new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries)];
        }
        return [];
    }

    private static string? StringProperty(uint node, DevPropKey key)
    {
        uint size = 0;
        var result = CM_Get_DevNode_PropertyW(node, &key, out var type, null, &size, 0);
        if (result != BufferSmall || size < 2)
            return null;
        var buffer = new byte[size];
        fixed (byte* bytes = buffer)
        {
            if (CM_Get_DevNode_PropertyW(node, &key, out type, bytes, &size, 0) != Success || type != TypeString)
                return null;
            return new string((char*)bytes).Trim() is { Length: > 0 } text ? text : null;
        }
    }

    private static bool? BooleanProperty(uint node, DevPropKey key)
    {
        byte value = 0;
        uint size = 1;
        return CM_Get_DevNode_PropertyW(node, &key, out var type, &value, &size, 0) == Success && type == TypeBoolean
            ? value != 0
            : null;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DevPropKey(Guid fmtid, uint pid)
    {
        public readonly Guid Fmtid = fmtid;
        public readonly uint Pid = pid;
    }

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_List_SizeW(out uint length, string filter, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_ID_ListW(string filter, char* buffer, uint length, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNodeW(out uint node, string id, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_PropertyW(uint node, DevPropKey* key, out uint type, byte* buffer, uint* size, uint flags);
}

/// <summary><c>devices bluetooth</c>: the paired list, with what the audio endpoints add (D-052).</summary>
public static class BluetoothList
{
    /// <summary>
    /// One row per paired address (an LE and a classic node of one phone merge). A device with audio endpoints can be
    /// connected here and takes Connected from them; another keeps its node's state. Endpoints of devices that are no
    /// longer paired are dropped (X-005).
    /// </summary>
    public static List<DeviceInfo> Merge(IEnumerable<BluetoothNode> nodes,
        IEnumerable<(string Address, string Name, bool Connected)> endpoints)
    {
        var audio = endpoints.GroupBy(e => KsBluetoothAudio.Hex(e.Address))
            .ToDictionary(g => g.Key, g => (Name: g.First().Name, Connected: g.Any(e => e.Connected)));
        return
        [
            .. nodes.GroupBy(n => KsBluetoothAudio.Hex(n.Address)).Select(g =>
            {
                var hasAudio = audio.TryGetValue(g.Key, out var a);
                var name = g.Select(n => n.Name).FirstOrDefault(n => n.Length > 0) ?? (hasAudio ? a.Name : "");
                bool? connected = hasAudio ? a.Connected
                    : g.Any(n => n.Connected == true) ? true
                    : g.Any(n => n.Connected == false) ? false
                    : null;
                return new DeviceInfo
                {
                    Name = KsBluetoothAudio.Format(g.Key),
                    Detail = name.Length > 0 ? name : null,
                    State = connected switch { true => "connected", false => "not connected", null => null },
                    NotConnectable = !hasAudio,
                };
            }).OrderBy(d => d.Detail ?? d.Name, StringComparer.CurrentCultureIgnoreCase),
        ];
    }
}
