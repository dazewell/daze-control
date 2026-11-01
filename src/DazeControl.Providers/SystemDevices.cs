using DazeControl.Core.Model;
using DazeControl.Ipc;
using DazeControl.Providers.Audio;
using DazeControl.Providers.Bluetooth;
using DazeControl.Providers.Display;
using DazeControl.Runtime.Control;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers;

/// <summary>The real PC's devices, read-only, for <c>dctl devices</c> and <c>dctl capture-display-layout</c>.</summary>
public sealed class SystemDevices(IDisplays displays) : IDeviceQueries
{
    public IReadOnlyList<string> Kinds { get; } = ["audio", "bluetooth", "displays", "adb"];

    public async Task<IReadOnlyList<DeviceInfo>> ListAsync(string kind, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case "audio":
                var list = new List<DeviceInfo>();
                foreach (var flow in (AudioFlow[])[AudioFlow.Output, AudioFlow.Input])
                {
                    var (endpoints, defaultId) = await CoreAudioEndpoints.ListAsync(flow, cancellationToken).ConfigureAwait(false);
                    list.AddRange(endpoints.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).Select(e => new DeviceInfo
                    {
                        Name = e.Name,
                        Id = e.Id,
                        Detail = (flow == AudioFlow.Output ? "Output" : "Input") + (e.Id == defaultId ? ", default" : ""),
                    }));
                }
                return list;
            case "bluetooth":
                var paired = await KsBluetoothAudio.ListAsync(cancellationToken).ConfigureAwait(false);
                return [.. paired.Select(d => new DeviceInfo
                {
                    Name = d.Address,
                    Detail = (d.Name.Length > 0 ? d.Name + ", " : "") + (d.Connected ? "connected" : "not connected"),
                })];
            case "displays":
                // A GPU reports every port it has; only those with a screen plugged in are worth naming.
                return [.. displays.Outputs().Where(o => o.Available).Select(o => new DeviceInfo
                {
                    Name = o.Port,
                    Detail = (o.Monitor.Length > 0 ? o.Monitor + ", " : "") + (o.Active ? "on" : "off"),
                })];
            case "adb":
                throw new StepFailedException("Fire TV devices arrive with the firetv steps.");
            default:
                throw new StepFailedException($"Say which devices: {string.Join(", ", Kinds)}.");
        }
    }

    public Task<DisplayLayout> CaptureDisplayLayoutAsync(string key, CancellationToken cancellationToken) =>
        Task.Run(() => displays.CaptureLayout(key), cancellationToken);
}
