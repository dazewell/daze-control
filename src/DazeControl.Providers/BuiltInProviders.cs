using DazeControl.Providers.Audio;
using DazeControl.Providers.Basic;
using DazeControl.Providers.Bluetooth;
using DazeControl.Providers.Display;
using DazeControl.Providers.Files;
using DazeControl.Providers.Android;
using DazeControl.Providers.Http;
using DazeControl.Providers.Network;
using DazeControl.Providers.Pc;
using DazeControl.Providers.Process;
using DazeControl.Providers.Service;
using DazeControl.Runtime.Control;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers;

/// <summary>The engine's composition root for step types (ARCHITECTURE → Adding a provider, step 3).</summary>
public static class BuiltInProviders
{
    /// <summary>What <c>dctl devices</c> and <c>dctl capture-display-layout</c> read.</summary>
    public static IDeviceQueries Devices()
    {
        var adb = new AdbProcess();
        var cache = new AndroidAppCache(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Core.AppInfo.FolderName, "android-apps"));
        var apps = new AndroidAppLister(new AndroidRemote(adb, TimeProvider.System), adb, cache);
        return new SystemDevices(new CcdDisplays(), adb, new CfgMgrPairedBluetooth(), apps);
    }

    /// <summary>
    /// The read-only fact probes (D-081) behind "Only if" and the check steps. Each reads on demand from the same
    /// queries as the steps; nothing is held between runs (idle = zero).
    /// </summary>
    public static IReadOnlyList<IFactProbe> Probes() =>
        [new BluetoothConnectedProbe(new SystemBluetoothState(new CfgMgrPairedBluetooth())), .. Probes(new CoreAudioEndpoints(), new WindowsProcesses(), new WindowsServices(), new WindowsNetworkState(), new WindowsPcState(), TimeProvider.System)];

    internal static IReadOnlyList<IFactProbe> Probes(IAudioEndpoints audio, IProcesses processes, IServices services,
        INetworkState network, IPcState pc, TimeProvider time) =>
    [
        new AudioDefaultProbe(audio),
        new ProcessRunningProbe(processes),
        new ServiceRunningProbe(services),
        new FileExistsProbe(),
        new WifiProbe(network),
        new CableProbe(network),
        new InternetProbe(network),
        new MeteredProbe(network),
        new VpnProbe(network),
        new PluggedInProbe(pc),
        new BatteryProbe(pc),
        new IdleProbe(pc),
        new LockedProbe(pc),
        new ScreenCountProbe(pc),
        new FreeSpaceProbe(pc),
        new TimeWindowProbe(time),
    ];

    /// <summary>The D-118 checks: step id → (fact, the <c>is</c> choice that means the fact is true).</summary>
    internal static readonly (string Step, string Fact, string Positive)[] StateChecks =
    [
        ("network.wifi", "network.wifi", "connected"),
        ("network.cable", "network.cable", "connected"),
        ("network.internet", "network.internet", "online"),
        ("network.metered", "network.metered", "metered"),
        ("network.vpn", "network.vpn", "connected"),
        ("power.source", "power.ac", "pluggedIn"),
        ("power.battery", "power.battery", "atLeast"),
        ("pc.idle", "pc.idle", "idle"),
        ("pc.locked", "pc.locked", "locked"),
        ("display.count", "display.count", "atLeast"),
        ("file.space", "file.space", "atLeast"),
        ("time.check", "time.window", "inside"),
    ];

    public static IReadOnlyList<IStepProvider> Create(TimeProvider time, INotifier notifier)
    {
        var flags = new FlagStore();
        var audio = new CoreAudioEndpoints();
        var bluetooth = new KsBluetoothAudio();
        var displays = new CcdDisplays();
        var android = new AndroidRemote(new AdbProcess(), time);
        var services = new WindowsServices();
        var probes = Probes(audio, new WindowsProcesses(), services, new WindowsNetworkState(), new WindowsPcState(), time).ToDictionary(p => p.Fact);
        return
        [
            new WaitProvider(time),
            new NotifyProvider(notifier),
            new MakeTextProvider(),
            new ValueCheckProvider(),
            new FlagSetProvider(flags),
            new FlagCheckProvider(flags),
            new ProcessRunProvider(),
            new ProcessOpenProvider(new ShellOpener()),
            new ProcessCloseProvider(new WindowsCloseTargets(), time),
            new PowerShellProvider(),
            new HttpRequestProvider(new WindowsCredentials()),
            new AudioSetDefaultProvider(audio),
            new AudioSetVolumeProvider(audio),
            new AudioMuteProvider(audio),
            new AudioMediaProvider(new WindowsMediaCommands()),
            new BluetoothConnectProvider(bluetooth),
            new BluetoothDisconnectProvider(bluetooth),
            new BluetoothCheckProvider(new BluetoothConnectedProbe(new SystemBluetoothState(new CfgMgrPairedBluetooth()))),
            new DisplayTopologyProvider(displays),
            new DisplayOutputsProvider(displays),
            new DisplayLayoutProvider(displays),
            new DisplayCheckProvider(displays),
            new DisplayScreensOffProvider(new WindowsMonitorPower(), time),
            new AndroidInputProvider(android),
            new AndroidKeysProvider(android),
            new AndroidSleepProvider(android),
            new AndroidStartProvider(android),
            new AndroidStopProvider(android),
            new AndroidConnectProvider(android),
            new AndroidWakeProvider(android),
            new AndroidMediaProvider(android),
            new AndroidSetVolumeProvider(android),
            new AndroidChangeVolumeProvider(android),
            new AndroidMuteProvider(android),
            new AndroidSetBrightnessProvider(android),
            new AndroidOpenProvider(android),
            new AndroidCheckProvider(android, new WindowsProcesses()),
            new NetworkWakeProvider(new WindowsWakeNetwork()),
            new ServiceStartProvider(services, time),
            new ServiceStopProvider(services, time),
            new FactCheckProvider("audio.check", probes["audio.isDefault"], "default"),
            new FactCheckProvider("process.check", probes["process.running"], "running"),
            new FactCheckProvider("service.check", probes["service.running"], "running"),
            new FactCheckProvider("file.check", probes["file.exists"], "exists"),
            .. StateChecks.Select(c => new FactCheckProvider(c.Step, probes[c.Fact], c.Positive)),
        ];
    }
}
