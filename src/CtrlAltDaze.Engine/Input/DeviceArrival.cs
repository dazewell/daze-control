using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace CtrlAltDaze.Engine.Input;

/// <summary>
/// Tells the reader a game controller came or went (D-056): a device-interface notification for the XUSB class (Xbox
/// pads) and for HID nodes marked as XInput devices. Registered only while the gamepad source runs; the callback just
/// sets an event, so an idle engine does nothing here.
/// </summary>
internal sealed unsafe partial class DeviceArrival : IDisposable
{
    private static readonly Guid Xusb = new("EC87F1E3-C13B-4100-B5F7-8B84D54260CB");
    private static readonly Guid Hid = new("4D1E55B2-F16F-11CF-88CB-001111000030");

    private readonly Action _changed;
    private GCHandle _self;
    private nint _xusb, _hid;

    public DeviceArrival(Action changed)
    {
        _changed = changed;
        _self = GCHandle.Alloc(this);
        _xusb = Register(Xusb);
        _hid = Register(Hid);
    }

    public void Dispose()
    {
        if (_xusb != 0)
            _ = CM_Unregister_Notification(_xusb);
        if (_hid != 0)
            _ = CM_Unregister_Notification(_hid);
        _xusb = _hid = 0;
        if (_self.IsAllocated)
            _self.Free();
    }

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
            var classGuid = *(Guid*)(data + 8);
            if (classGuid != Xusb)
            {
                // HID: only XInput-class nodes ("IG_" in the path) are pads we read; the rest is mice and keyboards.
                var link = new string((char*)(data + 24));
                if (!link.Contains("IG_", StringComparison.OrdinalIgnoreCase))
                    return 0;
            }
            if (GCHandle.FromIntPtr((nint)context).Target is DeviceArrival self)
                self._changed();
        }
        catch (Exception)
        {
            // Never unwind into the configuration manager; the next arrival or the 60 s of nothing is harmless.
        }
        return 0;
    }

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Register_Notification(byte* filter, void* context,
        delegate* unmanaged[Stdcall]<nint, void*, int, byte*, uint, uint> callback, nint* handle);

    [LibraryImport("cfgmgr32.dll")]
    private static partial uint CM_Unregister_Notification(nint handle);
}
