using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using DazeControl.Core.Model;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.Audio.Endpoints;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace DazeControl.Providers.Audio;

/// <summary>
/// Core Audio through raw CsWin32 vtables (no built-in COM marshalling under Native AOT, D-022). Every call runs on
/// its own short-lived MTA thread (<see cref="ComThread"/>), so nothing COM stays resident between steps (D-009).
/// CsWin32's COM methods throw on a failed HRESULT; <see cref="ComThread"/> turns that into a step failure.
/// </summary>
public sealed unsafe class CoreAudioEndpoints : IAudioEndpoints
{
    // Undocumented IPolicyConfig, what Sound settings uses to set the default endpoint (OSD D-019).
    private static readonly Guid PolicyConfigClsid = new("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9");
    private static readonly Guid PolicyConfigIid = new("F8679F50-850A-41CF-9C72-430F290290C8");
    private const int SetDefaultEndpointSlot = 3 + 10; // IUnknown, then ten format/period/share-mode slots

    public Task<AudioEndpoint> WaitForAsync(string match, AudioFlow flow, CancellationToken cancellationToken) =>
        ComThread.RunAsync(() => WaitFor(match, flow, cancellationToken), cancellationToken);

    public Task<AudioEndpoint?> DefaultAsync(AudioFlow flow, CancellationToken cancellationToken) =>
        ComThread.RunAsync(() => Default(flow), cancellationToken);

    public Task SetDefaultAsync(string id, AudioRoles roles, CancellationToken cancellationToken) =>
        ComThread.RunAsync(() => SetDefault(id, roles), cancellationToken);

    public Task SetVolumeAsync(string id, float level, CancellationToken cancellationToken) =>
        ComThread.RunAsync(() => WithVolume(id, volume => volume->SetMasterVolumeLevelScalar(level, null)), cancellationToken);

    public Task SetMuteAsync(string id, bool muted, CancellationToken cancellationToken) =>
        ComThread.RunAsync(() => WithVolume(id, volume => volume->SetMute(muted, null)), cancellationToken);

    public Task<IReadOnlyList<AudioEndpoint?>> DefaultsAsync(AudioFlow flow, AudioRoles roles, CancellationToken cancellationToken) =>
        ComThread.RunAsync<IReadOnlyList<AudioEndpoint?>>(() => [.. Roles(roles).Select(role => Default(flow, role))], cancellationToken);

    /// <summary>The friendly name of endpoint <paramref name="id"/>, or null when it can't be read (T24.6: the new default's name for the event).</summary>
    public static Task<string?> NameOfAsync(string id, CancellationToken cancellationToken) =>
        ComThread.RunAsync<string?>(() =>
        {
            using var enumerator = Enumerator.Create();
            IMMDevice* device;
            try
            {
                enumerator.Value->GetDevice(id, &device);
            }
            catch (COMException)
            {
                return null;
            }
            try
            {
                return Describe(device).Name is { Length: > 0 } name ? name : null;
            }
            finally
            {
                device->Release();
            }
        }, cancellationToken);

    private static AudioEndpoint? Default(AudioFlow flow, ERole role = ERole.eConsole)
    {
        using var enumerator = Enumerator.Create();
        IMMDevice* device;
        try
        {
            enumerator.Value->GetDefaultAudioEndpoint(DataFlow(flow), role, &device);
        }
        catch (COMException)
        {
            return null; // E_NOTFOUND: no device of that kind
        }
        try
        {
            return Describe(device);
        }
        finally
        {
            device->Release();
        }
    }

    private static bool SetDefault(string id, AudioRoles roles)
    {
        void* policy;
        var clsid = PolicyConfigClsid;
        var iid = PolicyConfigIid;
        PInvoke.CoCreateInstance(&clsid, null, CLSCTX.CLSCTX_ALL, &iid, &policy).ThrowOnFailure();
        try
        {
            var setDefault = (delegate* unmanaged[Stdcall]<void*, char*, int, HRESULT>)(*(void***)policy)[SetDefaultEndpointSlot];
            fixed (char* text = id)
            {
                foreach (var role in Roles(roles))
                    setDefault(policy, text, (int)role).ThrowOnFailure();
            }
        }
        finally
        {
            ((IUnknown*)policy)->Release();
        }
        return true;
    }

    private delegate void VolumeAction(IAudioEndpointVolume* volume);

    private static bool WithVolume(string id, VolumeAction action)
    {
        using var enumerator = Enumerator.Create();
        IMMDevice* device;
        enumerator.Value->GetDevice(id, &device);
        try
        {
            device->Activate(typeof(IAudioEndpointVolume).GUID, CLSCTX.CLSCTX_ALL, null, out var volume);
            try
            {
                action((IAudioEndpointVolume*)volume);
            }
            finally
            {
                ((IUnknown*)volume)->Release();
            }
        }
        finally
        {
            device->Release();
        }
        return true;
    }

    private static AudioEndpoint WaitFor(string match, AudioFlow flow, CancellationToken cancellationToken)
    {
        using var enumerator = Enumerator.Create();
        using var changed = new SemaphoreSlim(0);
        using var client = DeviceChangeClient.Create(() => changed.Release());
        enumerator.Value->RegisterEndpointNotificationCallback(client.Pointer);
        try
        {
            while (true)
            {
                if (Find(enumerator.Value, match, flow) is { } found)
                    return found;
                // Event-driven: re-check only when Windows reports a device change (D-009); the token is the timeout.
                changed.Wait(cancellationToken);
            }
        }
        finally
        {
            enumerator.Value->UnregisterEndpointNotificationCallback(client.Pointer);
        }
    }

    private static AudioEndpoint? Find(IMMDeviceEnumerator* enumerator, string match, AudioFlow flow) =>
        Active(enumerator, flow).FirstOrDefault(e => e.Name.Contains(match, StringComparison.OrdinalIgnoreCase));

    /// <summary>Read-only, for <c>dctl devices audio</c>: the connected endpoints of one flow and the default's id.</summary>
    internal static Task<(List<AudioEndpoint> Endpoints, string? DefaultId)> ListAsync(AudioFlow flow, CancellationToken cancellationToken) =>
        ComThread.RunAsync(() =>
        {
            using var enumerator = Enumerator.Create();
            return (Active(enumerator.Value, flow), Default(flow)?.Id);
        }, cancellationToken);

    private static List<AudioEndpoint> Active(IMMDeviceEnumerator* enumerator, AudioFlow flow)
    {
        var found = new List<AudioEndpoint>();
        IMMDeviceCollection* devices;
        enumerator->EnumAudioEndpoints(DataFlow(flow), DEVICE_STATE.DEVICE_STATE_ACTIVE, &devices);
        try
        {
            devices->GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                IMMDevice* device;
                try
                {
                    devices->Item(i, &device);
                }
                catch (COMException)
                {
                    continue; // removed while we listed
                }
                try
                {
                    found.Add(Describe(device));
                }
                finally
                {
                    device->Release();
                }
            }
            return found;
        }
        finally
        {
            devices->Release();
        }
    }

    internal static AudioEndpoint Describe(IMMDevice* device)
    {
        device->GetId(out var id);
        string text;
        try
        {
            text = id.ToString();
        }
        finally
        {
            Marshal.FreeCoTaskMem((nint)id.Value);
        }
        return new AudioEndpoint(text, FriendlyName(device));
    }

    private static string FriendlyName(IMMDevice* device)
    {
        IPropertyStore* store;
        PROPVARIANT value;
        try
        {
            device->OpenPropertyStore(STGM.STGM_READ, &store);
        }
        catch (COMException)
        {
            return "";
        }
        try
        {
            store->GetValue(PInvoke.PKEY_Device_FriendlyName, out value);
        }
        catch (COMException)
        {
            return "";
        }
        finally
        {
            store->Release();
        }
        try
        {
            return value.vt == Windows.Win32.System.Variant.VARENUM.VT_LPWSTR ? value.pwszVal.ToString() : "";
        }
        finally
        {
            PInvoke.PropVariantClear(ref value);
        }
    }

    private static EDataFlow DataFlow(AudioFlow flow) => flow == AudioFlow.Input ? EDataFlow.eCapture : EDataFlow.eRender;

    private static ERole[] Roles(AudioRoles roles) => roles switch
    {
        AudioRoles.Console => [ERole.eConsole, ERole.eMultimedia],
        AudioRoles.Communications => [ERole.eCommunications],
        _ => [ERole.eConsole, ERole.eMultimedia, ERole.eCommunications],
    };

    internal readonly struct Enumerator : IDisposable
    {
        public IMMDeviceEnumerator* Value { get; private init; }

        public static Enumerator Create()
        {
            PInvoke.CoCreateInstance<IMMDeviceEnumerator>(typeof(MMDeviceEnumerator).GUID, null, CLSCTX.CLSCTX_ALL, out var pointer)
                .ThrowOnFailure();
            return new Enumerator { Value = pointer };
        }

        public void Dispose() => Value->Release();
    }


    /// <summary>
    /// A hand-built IMMNotificationClient (no COM marshalling under AOT): a vtable of static callbacks and a
    /// GCHandle to the callback. Lifetime is ours, not refcounted: freed only after Unregister has returned.
    /// </summary>
    internal sealed class DeviceChangeClient : IDisposable
    {
        private static readonly HRESULT Ok = (HRESULT)0;
        private static readonly HRESULT NoInterface = (HRESULT)unchecked((int)0x80004002);
        private static readonly void** Vtable = CreateVtable();
        private readonly GCHandle _handle;

        public IMMNotificationClient* Pointer { get; }

        private readonly GCHandle _defaultHandle;

        private DeviceChangeClient(Action changed, bool onDefault, Action<AudioFlow, string>? defaultChanged)
        {
            _handle = GCHandle.Alloc(changed);
            var instance = (nint*)NativeMemory.Alloc((nuint)(4 * sizeof(nint)));
            instance[0] = (nint)Vtable;
            instance[1] = GCHandle.ToIntPtr(_handle);
            instance[2] = onDefault ? 1 : 0; // also signal on a new default endpoint (the default-changed trigger, T20.4)
            if (defaultChanged is not null)
                _defaultHandle = GCHandle.Alloc(defaultChanged);
            instance[3] = defaultChanged is null ? 0 : GCHandle.ToIntPtr(_defaultHandle); // which endpoint became the default (T24.6)
            Pointer = (IMMNotificationClient*)instance;
        }

        public static DeviceChangeClient Create(Action changed, bool onDefault = false, Action<AudioFlow, string>? defaultChanged = null) =>
            new(changed, onDefault, defaultChanged);

        public void Dispose()
        {
            NativeMemory.Free(Pointer);
            _handle.Free();
            if (_defaultHandle.IsAllocated)
                _defaultHandle.Free();
        }

        private static void** CreateVtable()
        {
            var table = (void**)NativeMemory.Alloc((nuint)(8 * sizeof(void*)));
            table[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, HRESULT>)&QueryInterface;
            table[1] = (delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
            table[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&Release;
            table[3] = (delegate* unmanaged[Stdcall]<void*, char*, uint, HRESULT>)&OnDeviceStateChanged;
            table[4] = (delegate* unmanaged[Stdcall]<void*, char*, HRESULT>)&OnDeviceAdded;
            table[5] = (delegate* unmanaged[Stdcall]<void*, char*, HRESULT>)&OnDeviceRemoved;
            table[6] = (delegate* unmanaged[Stdcall]<void*, EDataFlow, ERole, char*, HRESULT>)&OnDefaultDeviceChanged;
            table[7] = (delegate* unmanaged[Stdcall]<void*, char*, PROPERTYKEY, HRESULT>)&OnPropertyValueChanged;
            return table;
        }

        private static void Signal(void* self)
        {
            try
            {
                ((Action)GCHandle.FromIntPtr(((nint*)self)[1]).Target!)();
            }
            catch (ObjectDisposedException)
            {
                // The waiter finished between the event and this call.
            }
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static HRESULT QueryInterface(void* self, Guid* iid, void** result)
        {
            if (*iid == typeof(IMMNotificationClient).GUID || *iid == typeof(IUnknown).GUID)
            {
                *result = self;
                return Ok;
            }
            *result = null;
            return NoInterface;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint AddRef(void* self) => 1;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static uint Release(void* self) => 1;

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static HRESULT OnDeviceStateChanged(void* self, char* id, uint state)
        {
            Signal(self);
            return Ok;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static HRESULT OnDeviceAdded(void* self, char* id)
        {
            Signal(self);
            return Ok;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static HRESULT OnDeviceRemoved(void* self, char* id)
        {
            // A disconnect wait ends when the device's endpoints are gone, not only inactive.
            Signal(self);
            return Ok;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static HRESULT OnDefaultDeviceChanged(void* self, EDataFlow flow, ERole role, char* id)
        {
            if (((nint*)self)[2] == 1) // every role: the trigger means any default sound device change (the service debounces the repeats)
            {
                if (((nint*)self)[3] != 0 && id is not null && flow is EDataFlow.eRender or EDataFlow.eCapture)
                {
                    try
                    {
                        ((Action<AudioFlow, string>)GCHandle.FromIntPtr(((nint*)self)[3]).Target!)(flow == EDataFlow.eCapture ? AudioFlow.Input : AudioFlow.Output, new string(id));
                    }
                    catch (Exception)
                    {
                        // Never unwind into the audio service.
                    }
                }
                else
                {
                    Signal(self);
                }
            }
            return Ok;
        }

        [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
        private static HRESULT OnPropertyValueChanged(void* self, char* id, PROPERTYKEY key)
        {
            // The friendly name can arrive after the endpoint turns active (Bluetooth), so re-check on it too.
            Signal(self);
            return Ok;
        }
    }
}
