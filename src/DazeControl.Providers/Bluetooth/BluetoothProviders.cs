using DazeControl.Core.Catalog;
using DazeControl.Core.Model;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Bluetooth;

public enum BluetoothChange
{
    Done,
    AlreadyDone,
}

/// <summary>
/// Connects and disconnects paired Bluetooth audio devices without admin (D-008, OSD D-023). Each call waits until
/// the device's audio endpoints show the change, or until the token fires.
/// </summary>
public interface IBluetoothAudio
{
    Task<BluetoothChange> ConnectAsync(BluetoothDevice device, CancellationToken cancellationToken);

    Task<BluetoothChange> DisconnectAsync(BluetoothDevice device, CancellationToken cancellationToken);
}

internal static class BluetoothSteps
{
    public static BluetoothDevice Device(StepContext context)
    {
        var key = context.RequiredText("device");
        return context.Library.ResolveBluetooth(key)
            ?? throw new StepFailedException(context.Library.Bound.FirstOrDefault(b => b.Key == key) is { } bound
                ? $"The bound device \"{key}\" has no Bluetooth device \"{bound.Bluetooth}\" in your Library."
                : Library.Missing(LibraryKind.Bluetooth, key));
    }

    /// <summary>The name the user knows the device by in messages.</summary>
    public static string Title(BluetoothDevice device) => device.Name ?? device.Key;
}

/// <summary><c>bluetooth.connect</c>: connects a paired device; already connected counts as success.</summary>
public sealed class BluetoothConnectProvider(IBluetoothAudio bluetooth) : IStepProvider
{
    public string Id => "bluetooth.connect";

    public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        await bluetooth.ConnectAsync(BluetoothSteps.Device(context), cancellationToken).ConfigureAwait(false);
        context.SetDevice("device", context.RequiredText("device"), LibraryKind.Bluetooth);
    }
}

/// <summary><c>bluetooth.disconnect</c>: disconnects a device; already disconnected counts as success.</summary>
public sealed class BluetoothDisconnectProvider(IBluetoothAudio bluetooth) : IStepProvider
{
    public string Id => "bluetooth.disconnect";

    public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        await bluetooth.DisconnectAsync(BluetoothSteps.Device(context), cancellationToken).ConfigureAwait(false);
        context.SetDevice("device", context.RequiredText("device"), LibraryKind.Bluetooth);
    }
}

/// <summary>Whether a paired device is connected right now; null when Windows doesn't say (D-081).</summary>
public interface IBluetoothState
{
    Task<bool?> IsConnectedAsync(BluetoothDevice device, CancellationToken cancellationToken);
}

/// <summary>The state <c>dctl devices bluetooth</c> and the picker show (D-052): the same queries, read on demand.</summary>
public sealed class SystemBluetoothState(IPairedBluetooth paired) : IBluetoothState
{
    public async Task<bool?> IsConnectedAsync(BluetoothDevice device, CancellationToken cancellationToken)
    {
        var endpoints = await KsBluetoothAudio.ListAsync(cancellationToken).ConfigureAwait(false);
        var list = BluetoothList.Merge(await Task.Run(paired.Present, cancellationToken).ConfigureAwait(false), endpoints);
        var name = KsBluetoothAudio.Format(KsBluetoothAudio.Hex(device.Address));
        return list.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase))?.State switch
        {
            "connected" => true,
            "not connected" => false,
            _ => null,
        };
    }
}

/// <summary>Fact <c>bluetooth.connected</c> for the step's <c>device</c>: backs "Only if" on Connect / Disconnect and the check step.</summary>
public sealed class BluetoothConnectedProbe(IBluetoothState state) : IFactProbe
{
    public string Fact => Core.Catalog.ProviderCatalog.BluetoothConnected;

    public async Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken)
    {
        var device = BluetoothSteps.Device(context);
        var title = BluetoothSteps.Title(device);
        // Unknown means "cannot tell": throw, so a condition runs the step and a check says so (D-081).
        var connected = await state.IsConnectedAsync(device, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Windows doesn't say whether {title} is connected.");
        return new FactReading(connected, connected ? $"{title} is connected" : $"{title} isn't connected");
    }
}

/// <summary><c>bluetooth.check</c>: fails unless the device is (or isn't) connected, so failure edges branch (D-081).</summary>
public sealed class BluetoothCheckProvider(IFactProbe probe) : IStepProvider
{
    public string Id => "bluetooth.check";

    public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        var expected = context.Text("is") != "disconnected";
        var reading = await probe.ReadAsync(context, cancellationToken).ConfigureAwait(false);
        context.SetValue("result", reading.Text);
        if (reading.Value != expected)
            throw new CheckNotMetException(reading.Text + ".");
    }
}
