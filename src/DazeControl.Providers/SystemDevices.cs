using DazeControl.Core.Model;
using DazeControl.Ipc;
using DazeControl.Providers.Audio;
using DazeControl.Providers.Bluetooth;
using DazeControl.Providers.Display;
using DazeControl.Providers.Android;
using DazeControl.Providers.Http;
using DazeControl.Providers.Service;
using DazeControl.Runtime.Control;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers;

/// <summary>The real PC's devices, read-only, for <c>dctl devices</c> and <c>dctl capture-display-layout</c>.</summary>
public sealed class SystemDevices(IDisplays displays, IAdb adb, IPairedBluetooth paired, AndroidAppLister? apps = null) : IDeviceQueries
{
    public IReadOnlyList<string> Kinds { get; } = ["audio", "bluetooth", "displays", "adb", "services", "credentials", "wifi"];

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
                var audioEndpoints = await KsBluetoothAudio.ListAsync(cancellationToken).ConfigureAwait(false);
                return BluetoothList.Merge(await Task.Run(paired.Present, cancellationToken).ConfigureAwait(false), audioEndpoints);
            case "displays":
                // A GPU reports every port it has; only those with a screen plugged in are worth naming.
                return [.. displays.Outputs().Where(o => o.Available).Select(o => new DeviceInfo
                {
                    Name = o.Port,
                    Id = o.DevicePath.Length > 0 ? o.DevicePath : null,
                    Detail = (o.Monitor.Length > 0 ? o.Monitor + ", " : "") + (o.Active ? "on" : "off"),
                    State = o.Active ? "on" : "off",
                    Value = o.Monitor.Length > 0 ? o.Monitor : null,
                })];
            case "services":
                return await Task.Run(ServiceList.List, cancellationToken).ConfigureAwait(false);
            case "wifi":
                return [.. (await Task.Run(Network.WindowsNetworkState.WifiNetworks, cancellationToken).ConfigureAwait(false))
                    .Select(n => new DeviceInfo { Name = n.Name, Detail = n.Connected ? "Connected now" : "Saved on this PC", Value = n.Name })];
            case "credentials":
                return [.. (await Task.Run(WindowsCredentials.Names, cancellationToken).ConfigureAwait(false))
                    .Select(name => new DeviceInfo { Name = name, Detail = "Saved in Windows", Value = name })];
            case "adb":
                var result = await adb.RunAsync(AndroidRemote.DefaultAdb, ["devices"], cancellationToken).ConfigureAwait(false);
                if (result.ExitCode != 0)
                    throw new StepFailedException($"adb devices failed: {result.LastError}");
                return AdbDevices(result.Output);
            default:
                throw new StepFailedException($"Say which devices: {string.Join(", ", Kinds)}.");
        }
    }

    /// <summary>"List of devices attached", then "192.168.1.50:5555&lt;tab&gt;device" per device.</summary>
    internal static IReadOnlyList<DeviceInfo> AdbDevices(string output) =>
    [
        .. output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => !line.StartsWith("List of devices", StringComparison.Ordinal) && !line.StartsWith('*'))
            .Select(line => line.Split('\t', ' ').Where(part => part.Length > 0).ToArray())
            .Where(parts => parts.Length >= 2)
            .Select(parts => new DeviceInfo { Name = parts[0], Detail = parts[1] == "device" ? "connected" : parts[1] }),
    ];

    public Task<DisplayLayout> CaptureDisplayLayoutAsync(string key, CancellationToken cancellationToken) =>
        Task.Run(() => displays.CaptureLayout(key), cancellationToken);

    public async Task<IReadOnlyList<DeviceInfo>> AndroidAppsAsync(string address, string? adbPath, bool refresh, CancellationToken cancellationToken)
    {
        if (apps is null)
            throw new StepFailedException("This engine can't list Android apps.");
        var list = await apps.ListAsync(address, adbPath, refresh, cancellationToken).ConfigureAwait(false);
        return [.. list.Select(a => new DeviceInfo { Name = a.Name, Detail = a.Package, Value = a.Package })];
    }
}
