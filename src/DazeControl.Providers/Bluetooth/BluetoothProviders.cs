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
        return context.Library.Bluetooth.FirstOrDefault(d => d.Key == key)
            ?? throw new StepFailedException($"No Bluetooth device called \"{key}\" in the library.");
    }

    /// <summary>The name the user knows the device by in messages.</summary>
    public static string Title(BluetoothDevice device) => device.Name ?? device.Key;
}

/// <summary><c>bluetooth.connect</c>: connects a paired device; already connected counts as success.</summary>
public sealed class BluetoothConnectProvider(IBluetoothAudio bluetooth) : IStepProvider
{
    public string Id => "bluetooth.connect";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken) =>
        bluetooth.ConnectAsync(BluetoothSteps.Device(context), cancellationToken);
}

/// <summary><c>bluetooth.disconnect</c>: disconnects a device; already disconnected counts as success.</summary>
public sealed class BluetoothDisconnectProvider(IBluetoothAudio bluetooth) : IStepProvider
{
    public string Id => "bluetooth.disconnect";

    public Task ExecuteAsync(StepContext context, CancellationToken cancellationToken) =>
        bluetooth.DisconnectAsync(BluetoothSteps.Device(context), cancellationToken);
}
