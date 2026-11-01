using DazeControl.Providers.Audio;
using DazeControl.Providers.Basic;
using DazeControl.Providers.Bluetooth;
using DazeControl.Providers.Display;
using DazeControl.Providers.Http;
using DazeControl.Providers.Process;
using DazeControl.Runtime.Control;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers;

/// <summary>The engine's composition root for step types (ARCHITECTURE → Adding a provider, step 3).</summary>
public static class BuiltInProviders
{
    /// <summary>What <c>dctl devices</c> and <c>dctl capture-display-layout</c> read.</summary>
    public static IDeviceQueries Devices() => new SystemDevices(new CcdDisplays());

    public static IReadOnlyList<IStepProvider> Create(TimeProvider time, INotifier notifier)
    {
        var flags = new FlagStore();
        var audio = new CoreAudioEndpoints();
        var bluetooth = new KsBluetoothAudio();
        var displays = new CcdDisplays();
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
            new DisplayTopologyProvider(displays),
            new DisplayOutputsProvider(displays),
            new DisplayLayoutProvider(displays),
        ];
    }
}
