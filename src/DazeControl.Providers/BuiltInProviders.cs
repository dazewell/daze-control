using DazeControl.Providers.Audio;
using DazeControl.Providers.Basic;
using DazeControl.Providers.Bluetooth;
using DazeControl.Providers.Display;
using DazeControl.Providers.Process;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers;

/// <summary>The engine's composition root for step types (ARCHITECTURE → Adding a provider, step 3).</summary>
public static class BuiltInProviders
{
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
            new AudioSetDefaultProvider(audio),
            new AudioSetVolumeProvider(audio),
            new AudioMuteProvider(audio),
            new BluetoothConnectProvider(bluetooth),
            new BluetoothDisconnectProvider(bluetooth),
            new DisplayTopologyProvider(displays),
            new DisplayOutputsProvider(displays),
        ];
    }
}
