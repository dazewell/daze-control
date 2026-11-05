using DazeControl.Providers.Audio;
using DazeControl.Providers.Basic;
using DazeControl.Providers.Bluetooth;
using DazeControl.Providers.Display;
using DazeControl.Providers.Android;
using DazeControl.Providers.Http;
using DazeControl.Providers.Process;
using DazeControl.Providers.Service;
using DazeControl.Runtime.Control;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers;

/// <summary>The engine's composition root for step types (ARCHITECTURE → Adding a provider, step 3).</summary>
public static class BuiltInProviders
{
    /// <summary>What <c>dctl devices</c> and <c>dctl capture-display-layout</c> read.</summary>
    public static IDeviceQueries Devices() => new SystemDevices(new CcdDisplays(), new AdbProcess(), new CfgMgrPairedBluetooth());

    /// <summary>The read-only fact probes behind "Only if" and the check steps (D-081).</summary>
    public static IReadOnlyList<IFactProbe> Probes() => [new BluetoothConnectedProbe(new SystemBluetoothState(new CfgMgrPairedBluetooth()))];

    public static IReadOnlyList<IStepProvider> Create(TimeProvider time, INotifier notifier)
    {
        var flags = new FlagStore();
        var audio = new CoreAudioEndpoints();
        var bluetooth = new KsBluetoothAudio();
        var displays = new CcdDisplays();
        var android = new AndroidRemote(new AdbProcess(), time);
        var services = new WindowsServices();
        return
        [
            new WaitProvider(time),
            new NotifyProvider(notifier),
            new FlagSetProvider(flags),
            new FlagCheckProvider(flags),
            new ProcessRunProvider(),
            new PowerShellProvider(),
            new HttpRequestProvider(new WindowsCredentials()),
            new AudioSetDefaultProvider(audio),
            new AudioSetVolumeProvider(audio),
            new AudioMuteProvider(audio),
            new BluetoothConnectProvider(bluetooth),
            new BluetoothDisconnectProvider(bluetooth),
            new BluetoothCheckProvider(new BluetoothConnectedProbe(new SystemBluetoothState(new CfgMgrPairedBluetooth()))),
            new DisplayTopologyProvider(displays),
            new DisplayOutputsProvider(displays),
            new DisplayLayoutProvider(displays),
            new AndroidInputProvider(android),
            new AndroidKeysProvider(android),
            new AndroidSleepProvider(android),
            new ServiceStartProvider(services, time),
            new ServiceStopProvider(services, time),
        ];
    }
}
