using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DazeControl.Runtime.Triggers;

namespace DazeControl.Engine.Triggers;

/// <summary>
/// <c>device.added/removed</c> (M20, T20.4): a device-interface notification (cfgmgr32, like
/// <see cref="Input.DeviceArrival"/>) for USB devices and for the Bluetooth port driver's interface, which Windows
/// creates when a paired device connects and removes when it disconnects. Registered only while loaded; the callback
/// runs on a system thread and only raises. No polling, no admin.
/// </summary>
internal sealed unsafe partial class DeviceSource : ITriggerSource
{
    private static readonly Guid UsbDevice = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    private static readonly Guid BluetoothPort = new("0850302A-B344-4FDA-9BE9-90576B8D46F0");

    private readonly Lock _lock = new();
    private GCHandle _self;
    private nint _usb, _bluetooth;

    public IReadOnlyList<string> Events { get; } = ["device.added", "device.removed"];

    public event Action<TriggerEvent>? Raised;

    public void Load()
    {
        lock (_lock)
        {
            if (_self.IsAllocated)
                return;
            _self = GCHandle.Alloc(this);
            _usb = Register(UsbDevice);
            _bluetooth = Register(BluetoothPort);
            if (_usb == 0 && _bluetooth == 0)
            {
                _self.Free();
                throw new InvalidOperationException("Couldn't register for device notifications.");
            }
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
                self.Raised?.Invoke(new TriggerEvent(action == 0 ? "device.added" : "device.removed"));
        }
        catch (Exception)
        {
            // Never unwind into the configuration manager.
        }
        return 0;
    }

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Register_Notification(byte* filter, void* context,
        delegate* unmanaged[Stdcall]<nint, void*, int, byte*, uint, uint> callback, nint* handle);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Unregister_Notification(nint handle);
}
