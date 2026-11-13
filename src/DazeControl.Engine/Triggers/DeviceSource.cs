using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DazeControl.Runtime.Triggers;

namespace DazeControl.Engine.Triggers;

/// <summary>
/// <c>device.added/removed</c> (M20, T20.4): a device-interface notification (cfgmgr32, like
/// <see cref="Input.DeviceArrival"/>) for USB devices and for the Bluetooth port driver's interface, which Windows
/// creates when a paired device connects and removes when it disconnects. Registered only while loaded; the callback
/// runs on a system thread and only queues the work. No polling, no admin.
/// <para>
/// T24.6: the event's <see cref="TriggerEvent.Detail"/> is the device's name. It is read on the thread pool (not in the
/// callback) from the interface's device node. A removed device's node may already be unreadable, so the names of the
/// interfaces seen present are kept while the source is loaded (a few strings, dropped on <see cref="Unload"/>).
/// </para>
/// </summary>
internal sealed unsafe partial class DeviceSource : ITriggerSource
{
    private static readonly Guid UsbDevice = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    private static readonly Guid BluetoothPort = new("0850302A-B344-4FDA-9BE9-90576B8D46F0");

    private readonly Lock _lock = new();
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    private GCHandle _self;
    private nint _usb, _bluetooth;
    private int _generation;

    public IReadOnlyList<string> Events { get; } = ["device.added", "device.removed"];

    public event Action<TriggerEvent>? Raised;

    public void Load()
    {
        lock (_lock)
        {
            if (_self.IsAllocated)
                return;
            _self = GCHandle.Alloc(this);
            var generation = ++_generation;
            _usb = Register(UsbDevice);
            _bluetooth = Register(BluetoothPort);
            if (_usb == 0 && _bluetooth == 0)
            {
                _self.Free();
                throw new InvalidOperationException("Couldn't register for device notifications.");
            }
            ThreadPool.QueueUserWorkItem(_ => Seed(generation));
        }
    }

    public void Unload()
    {
        lock (_lock)
        {
            if (!_self.IsAllocated)
                return;
            // Unregister waits for a callback in flight, so the handle can be freed right after.
            if (_usb != 0)
                _ = CM_Unregister_Notification(_usb);
            if (_bluetooth != 0)
                _ = CM_Unregister_Notification(_bluetooth);
            _usb = _bluetooth = 0;
            _self.Free();
            _generation++;
            _names.Clear();
        }
    }

    public void Dispose() => Unload();

    private nint Register(Guid classGuid)
    {
        // CM_NOTIFY_FILTER: cbSize, Flags, FilterType (0 = device interface), Reserved, then the class GUID.
        byte* filter = stackalloc byte[416];
        new Span<byte>(filter, 416).Clear();
        *(uint*)filter = 416;
        *(Guid*)(filter + 16) = classGuid;
        nint handle;
        var result = CM_Register_Notification(filter, (void*)GCHandle.ToIntPtr(_self), &OnNotify, &handle);
        return result == 0 ? handle : 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnNotify(nint notify, void* context, int action, byte* data, uint size)
    {
        if (action is not (0 or 1)) // DEVICEINTERFACEARRIVAL, DEVICEINTERFACEREMOVAL
            return 0;
        try
        {
            if (GCHandle.FromIntPtr((nint)context).Target is DeviceSource self)
            {
                // CM_NOTIFY_EVENT_DATA: FilterType, Reserved, ClassGuid, then the NUL-terminated symbolic link.
                var link = size > 26 ? new string((char*)(data + 24)) : null;
                var added = action == 0;
                ThreadPool.QueueUserWorkItem(_ => self.Report(added, link));
            }
        }
        catch (Exception)
        {
            // Never unwind into the configuration manager.
        }
        return 0;
    }

    /// <summary>Raises the event with the device's name; one that can't be read raises without a detail.</summary>
    private void Report(bool added, string? link)
    {
        try
        {
            string? name = null;
            if (link is not null)
            {
                lock (_lock)
                {
                    if (!added && _names.Remove(link, out var known))
                        name = known;
                }
                name ??= NameOf(link);
                if (added && name is not null)
                {
                    lock (_lock)
                    {
                        if (_self.IsAllocated)
                            _names[link] = name;
                    }
                }
            }
            Raised?.Invoke(new TriggerEvent(added ? "device.added" : "device.removed", Detail: name));
        }
        catch (Exception)
        {
            // A thread-pool callback must not throw.
        }
    }

    /// <summary>Remembers the names of the interfaces present now, so a later removal still knows what went.</summary>
    private void Seed(int generation)
    {
        try
        {
            foreach (var classGuid in new[] { UsbDevice, BluetoothPort })
            {
                foreach (var link in Interfaces(classGuid))
                {
                    if (NameOf(link) is not { } name)
                        continue;
                    lock (_lock)
                    {
                        if (generation != _generation)
                            return; // unloaded (or reloaded) meanwhile
                        _names.TryAdd(link, name);
                    }
                }
            }
        }
        catch (Exception)
        {
            // Without the seed a removal of an old device raises without its name.
        }
    }

    private static List<string> Interfaces(Guid classGuid)
    {
        List<string> links = [];
        if (CM_Get_Device_Interface_List_SizeW(out var chars, &classGuid, null, 0) != 0 || chars <= 1)
            return links;
        var buffer = new char[chars];
        fixed (char* p = buffer)
        {
            if (CM_Get_Device_Interface_ListW(&classGuid, null, p, chars, 0) != 0)
                return links;
        }
        links.AddRange(new string(buffer).Split('\0', StringSplitOptions.RemoveEmptyEntries));
        return links;
    }

    /// <summary>The device node's friendly name, else its display name; null when unreadable (gone, or not set yet).</summary>
    private static string? NameOf(string link)
    {
        var instance = ReadString(link, 0, InstanceIdKey);
        if (instance is null || CM_Locate_DevNodeW(out var node, instance, 0) != 0)
            return null;
        return ReadString(null, node, FriendlyNameKey) ?? ReadString(null, node, DisplayNameKey);
    }

    private static string? ReadString(string? link, uint node, DevPropKey key)
    {
        uint type = 0;
        uint size = 0;
        var result = link is not null
            ? CM_Get_Device_Interface_PropertyW(link, &key, &type, null, &size, 0)
            : CM_Get_DevNode_PropertyW(node, &key, &type, null, &size, 0);
        if (result != 0x1A || size < 4 || size > 1024) // CR_BUFFER_SMALL: the size it wants
            return null;
        var buffer = stackalloc byte[(int)size];
        result = link is not null
            ? CM_Get_Device_Interface_PropertyW(link, &key, &type, buffer, &size, 0)
            : CM_Get_DevNode_PropertyW(node, &key, &type, buffer, &size, 0);
        if (result != 0 || type != 0x12) // DEVPROP_TYPE_STRING
            return null;
        var text = new string((char*)buffer, 0, (int)size / 2).TrimEnd('\0').Trim();
        return text.Length == 0 ? null : text;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct DevPropKey(Guid id, uint pid)
    {
        public readonly Guid Id = id;
        public readonly uint Pid = pid;
    }

    private static readonly DevPropKey InstanceIdKey = new(new("78c34fc8-104a-4aca-9ea4-524d52996e57"), 256);
    private static readonly DevPropKey FriendlyNameKey = new(new("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
    private static readonly DevPropKey DisplayNameKey = new(new("b725f130-47ef-101a-a5f1-02608c9eebac"), 10);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Get_Device_Interface_PropertyW(string link, DevPropKey* key, uint* type, byte* buffer, uint* size, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_DevNode_PropertyW(uint node, DevPropKey* key, uint* type, byte* buffer, uint* size, uint flags);

    [LibraryImport("cfgmgr32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint CM_Locate_DevNodeW(out uint node, string instanceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Device_Interface_List_SizeW(out uint chars, Guid* classGuid, char* deviceId, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Get_Device_Interface_ListW(Guid* classGuid, char* deviceId, char* buffer, uint chars, uint flags);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Register_Notification(byte* filter, void* context,
        delegate* unmanaged[Stdcall]<nint, void*, int, byte*, uint, uint> callback, nint* handle);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Unregister_Notification(nint handle);
}
