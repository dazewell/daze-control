using System.Runtime.InteropServices;
using DazeControl.Core.Model;
using DazeControl.Providers.Audio;
using DazeControl.Runtime.Providers;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.KernelStreaming;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace DazeControl.Providers.Bluetooth;

/// <summary>
/// Bluetooth audio through the BtAudio KS one-shot properties, what Sound control panel's Connect/Disconnect send
/// (OSD D-023, D-008: no admin). A device is found by its address in its endpoints' device paths, widened to every
/// endpoint of the same container: on Intel Smart Sound offload only the hands-free endpoint acts (X-005).
/// </summary>
public sealed unsafe class KsBluetoothAudio : IBluetoothAudio
{
    // Built-in devices all share this container; it says nothing about which endpoints belong together.
    private static readonly Guid LocalMachine = new("00000000-0000-0000-ffff-ffffffffffff");

    // The endpoint's device paths; BTHENUM paths end in the address ("…&B8208EED48FC_C00000000"). Pid 2 on classic
    // endpoints, pid 39 on Intel offload ones (seen on the dev PC, 26300).
    private static readonly PROPERTYKEY[] PathKeys =
    [
        new() { fmtid = new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), pid = 2 },
        new() { fmtid = new Guid("b3f8fa53-0004-438e-9003-51a46e139bfc"), pid = 39 },
    ];

    public Task<BluetoothChange> ConnectAsync(BluetoothDevice device, CancellationToken cancellationToken) =>
        ComThread.RunAsync(() => Change(device, connect: true, cancellationToken), cancellationToken);

    public Task<BluetoothChange> DisconnectAsync(BluetoothDevice device, CancellationToken cancellationToken) =>
        ComThread.RunAsync(() => Change(device, connect: false, cancellationToken), cancellationToken);

    /// <summary>Read-only: how many audio endpoints the device has here, and how many are connected.</summary>
    internal static Task<(int Total, int Active)> InspectAsync(BluetoothDevice device, CancellationToken cancellationToken) =>
        ComThread.RunAsync(() =>
        {
            using var enumerator = CoreAudioEndpoints.Enumerator.Create();
            var endpoints = Endpoints(enumerator.Value, Hex(device.Address));
            return (endpoints.Count, endpoints.Count(e => e.Active));
        }, cancellationToken);

    private static BluetoothChange Change(BluetoothDevice device, bool connect, CancellationToken cancellationToken)
    {
        var address = Hex(device.Address);
        var title = BluetoothSteps.Title(device);
        using var enumerator = CoreAudioEndpoints.Enumerator.Create();
        using var changed = new SemaphoreSlim(0);
        using var client = CoreAudioEndpoints.DeviceChangeClient.Create(() => changed.Release());
        enumerator.Value->RegisterEndpointNotificationCallback(client.Pointer);
        try
        {
            var endpoints = Endpoints(enumerator.Value, address);
            if (endpoints.Count == 0)
                throw new StepFailedException($"{title} isn't paired with this PC as an audio device.");
            if (endpoints.Any(e => e.Active) == connect)
                return BluetoothChange.AlreadyDone;

            var requested = 0;
            foreach (var endpoint in endpoints.Where(e => e.Active != connect))
            {
                if (Send(enumerator.Value, endpoint.Id, connect ? KSPROPERTY_BTAUDIO.KSPROPERTY_ONESHOT_RECONNECT : KSPROPERTY_BTAUDIO.KSPROPERTY_ONESHOT_DISCONNECT))
                    requested++;
            }
            if (requested == 0)
                throw new StepFailedException($"{title} can't be {(connect ? "connected" : "disconnected")} from here.");

            // Event-driven (D-009): re-check on each device change; the step timeout ends the wait.
            while (Endpoints(enumerator.Value, address).Any(e => e.Active) != connect)
                changed.Wait(cancellationToken);
            return BluetoothChange.Done;
        }
        finally
        {
            enumerator.Value->UnregisterEndpointNotificationCallback(client.Pointer);
        }
    }

    /// <summary>"B8:20:8E:ED:48:FC" → "B8208EED48FC", as it appears in device paths.</summary>
    internal static string Hex(string address) =>
        string.Concat(address.Where(char.IsAsciiHexDigit)).ToUpperInvariant();

    private readonly record struct Endpoint(string Id, bool Active);

    private static List<Endpoint> Endpoints(IMMDeviceEnumerator* enumerator, string address)
    {
        var all = new List<(string Id, bool Active, Guid? Container, bool Match)>();
        IMMDeviceCollection* devices;
        enumerator->EnumAudioEndpoints(EDataFlow.eAll,
            DEVICE_STATE.DEVICE_STATE_ACTIVE | DEVICE_STATE.DEVICE_STATE_UNPLUGGED | DEVICE_STATE.DEVICE_STATE_NOTPRESENT, &devices);
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
                    continue;
                }
                try
                {
                    device->GetState(out var state);
                    var match = false;
                    foreach (var key in PathKeys)
                        match |= StringProperty(device, key)?.Contains(address, StringComparison.OrdinalIgnoreCase) == true;
                    all.Add((CoreAudioEndpoints.Describe(device).Id, state == DEVICE_STATE.DEVICE_STATE_ACTIVE, ContainerId(device), match));
                }
                finally
                {
                    device->Release();
                }
            }
        }
        finally
        {
            devices->Release();
        }
        var containers = all.Where(e => e.Match && e.Container is { } c && c != LocalMachine).Select(e => e.Container).ToHashSet();
        return [.. all.Where(e => e.Match || containers.Contains(e.Container)).Select(e => new Endpoint(e.Id, e.Active))];
    }

    /// <summary>
    /// Endpoint topology → connector → the adapter's KS filter as an IMMDevice → IKsControl (OSD D-023). Only starts
    /// the change and returns at once; false when this endpoint's driver doesn't take BtAudio requests.
    /// </summary>
    private static bool Send(IMMDeviceEnumerator* enumerator, string id, KSPROPERTY_BTAUDIO request)
    {
        IMMDevice* device = null;
        IDeviceTopology* topology = null;
        IConnector* connector = null;
        IMMDevice* filter = null;
        IKsControl* control = null;
        try
        {
            enumerator->GetDevice(id, &device);
            device->Activate(typeof(IDeviceTopology).GUID, CLSCTX.CLSCTX_ALL, null, out var activated);
            topology = (IDeviceTopology*)activated;
            topology->GetConnector(0, &connector);
            PWSTR filterId;
            connector->GetDeviceIdConnectedTo(&filterId);
            try
            {
                enumerator->GetDevice(filterId.ToString(), &filter);
            }
            finally
            {
                Marshal.FreeCoTaskMem((nint)filterId.Value);
            }
            filter->Activate(typeof(IKsControl).GUID, CLSCTX.CLSCTX_ALL, null, out activated);
            control = (IKsControl*)activated;

            var property = default(KSIDENTIFIER);
            property.Anonymous.Anonymous.Set = typeof(KSPROPSETID_BtAudio).GUID;
            property.Anonymous.Anonymous.Id = (uint)request;
            property.Anonymous.Anonymous.Flags = PInvoke.KSPROPERTY_TYPE_GET;
            uint returned;
            control->KsProperty(&property, (uint)sizeof(KSIDENTIFIER), null, 0, &returned);
            return true;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Wired/USB drivers answer with an error HRESULT; an old pairing's offload filter is gone (0x80070002/3,
            // which .NET throws as IO exceptions). Other endpoints of the device may still take it.
            return false;
        }
        finally
        {
            if (control is not null)
                control->Release();
            if (filter is not null)
                filter->Release();
            if (connector is not null)
                connector->Release();
            if (topology is not null)
                topology->Release();
            if (device is not null)
                device->Release();
        }
    }

    private static string? StringProperty(IMMDevice* device, PROPERTYKEY key)
    {
        if (!TryValue(device, key, out var value))
            return null;
        try
        {
            return value.vt == Windows.Win32.System.Variant.VARENUM.VT_LPWSTR ? value.pwszVal.ToString() : null;
        }
        finally
        {
            PInvoke.PropVariantClear(ref value);
        }
    }

    private static Guid? ContainerId(IMMDevice* device)
    {
        if (!TryValue(device, PInvoke.PKEY_Device_ContainerId, out var value))
            return null;
        try
        {
            return value.vt == Windows.Win32.System.Variant.VARENUM.VT_CLSID && value.puuid is not null ? *value.puuid : null;
        }
        finally
        {
            PInvoke.PropVariantClear(ref value);
        }
    }

    private static bool TryValue(IMMDevice* device, PROPERTYKEY key, out PROPVARIANT value)
    {
        value = default;
        IPropertyStore* store;
        try
        {
            device->OpenPropertyStore(STGM.STGM_READ, &store);
        }
        catch (COMException)
        {
            return false;
        }
        try
        {
            store->GetValue(key, out value);
            return true;
        }
        catch (COMException)
        {
            return false;
        }
        finally
        {
            store->Release();
        }
    }
}
