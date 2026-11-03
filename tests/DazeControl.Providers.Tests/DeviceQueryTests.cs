using DazeControl.Core.Catalog;
using DazeControl.Core.Yaml;
using DazeControl.Providers.Bluetooth;
using DazeControl.Providers.Http;
using DazeControl.Providers.Service;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Tests;

public class DeviceQueryTests
{
    private static bool DevPc => Environment.GetEnvironmentVariable("DAZE_DEV_PC") == "1";

    [Theory]
    [InlineData(@"{2}.\?\bthenum#{0000110b-0000-1000-8000-00805f9b34fb}_vid&0001009e#7&2f&0&B8208EED48FC_C00000000#{x}", "B8208EED48FC")]
    [InlineData(@"{1}.BTHENUM\{0000111e-0000-1000-8000-00805f9b34fb}_LOCALMFG&0002\7&1&0&8099e72aaba1_C00000000", "8099E72AABA1")]
    [InlineData(@"{2}.\?\hdaudio#func_01&ven_10ec#4&1&0001#{x}", null)]
    [InlineData(null, null)]
    public void AddressComesFromTheDevicePath(string? path, string? address) =>
        Assert.Equal(address, KsBluetoothAudio.AddressIn(path));

    [Fact]
    public void AddressIsFormattedLikeTheLibrary() =>
        Assert.Equal("B8:20:8E:ED:48:FC", KsBluetoothAudio.Format("B8208EED48FC"));

    [Fact]
    public void DeviceNameIsTheSharedPart() =>
        Assert.Equal("Technics EAH-AZ80", KsBluetoothAudio.DeviceName(["Headset (Technics EAH-AZ80 Hands-Free)", "Headphones (Technics EAH-AZ80)"]));

    [Theory]
    [InlineData(@"BTHLE\DEV_D9E08EC29685\7&38A9E24E&0&D9E08EC29685", "D9E08EC29685")]
    [InlineData(@"BTHENUM\DEV_8099e72aaba1\7&2B3B0905&0&BLUETOOTHDEVICE_8099E72AABA1", "8099E72AABA1")]
    [InlineData(@"BTHENUM\{0000110b-0000-1000-8000-00805f9b34fb}_VID&0001009e\7&2F&0&B8208EED48FC_C00000000", null)]
    [InlineData(@"BTHLE\DEV_D9E08EC2968\x", null)]
    public void PairedAddressComesFromTheDeviceNode(string id, string? address) =>
        Assert.Equal(address, CfgMgrPairedBluetooth.AddressOf(id));

    [Fact]
    public void PairedListDropsEndpointsOfUnpairedDevices()
    {
        var list = BluetoothList.Merge([new("B8208EED48FC", "Technics EAH-AZ80", false)],
            [("B8:20:8E:ED:48:FC", "Technics EAH-AZ80", true), ("11:22:33:44:55:66", "SOUNDPEATS H3", false)]);

        var technics = Assert.Single(list);
        Assert.Equal("B8:20:8E:ED:48:FC", technics.Name);
        Assert.Equal("Technics EAH-AZ80", technics.Detail);
        Assert.Equal("connected", technics.State); // the endpoints say, not the node
        Assert.False(technics.NotConnectable);
    }

    [Fact]
    public void PairedListMergesTheLeAndClassicNodesOfOneDevice()
    {
        var list = BluetoothList.Merge([new("9CAA5D2F5E60", "OPPO dazewell", false), new("9caa5d2f5e60", "OPPO dazewell", true)], []);

        var phone = Assert.Single(list);
        Assert.Equal("9C:AA:5D:2F:5E:60", phone.Name);
        Assert.Equal("connected", phone.State);
    }

    [Fact]
    public void PairedListKeepsDevicesWithoutAudioAsNotConnectable()
    {
        var list = BluetoothList.Merge([new("D9E08EC29685", "MX Master 3S", true), new("D30C5D7D6020", "MX Keys Mini", null),
            new("8099E72AABA1", "WH-1000XM5", false)], [("80:99:E7:2A:AB:A1", "WH-1000XM5", false)]);

        Assert.Equal(["MX Keys Mini", "MX Master 3S", "WH-1000XM5"], list.Select(d => d.Detail));
        Assert.Equal([true, true, false], list.Select(d => d.NotConnectable));
        Assert.Equal([null, "connected", "not connected"], list.Select(d => d.State));
    }

    [Fact]
    public void PairedNameFallsBackToTheEndpoints() =>
        Assert.Equal("Zone 301", Assert.Single(BluetoothList.Merge([new("4473D6E42902", "", null)], [("44:73:D6:E4:29:02", "Zone 301", false)])).Detail);

    [Fact]
    public void EveryDevicesSuggestionHasAKindTheEngineLists()
    {
        var kinds = BuiltInProviders.Devices().Kinds;
        var wanted = ProviderCatalog.All.SelectMany(d => d.Params).Select(p => p.Suggest)
            .Where(s => s?.Source == SuggestSource.Devices).Select(s => s!.Kind).Distinct();

        Assert.All(wanted, kind => Assert.Contains(kind, kinds));
    }

    [Fact]
    public async Task ServicesAreListedByNameReadOnly()
    {
        var services = await BuiltInProviders.Devices().ListAsync("services", TestContext.Current.CancellationToken);

        var rpc = Assert.Single(services, s => string.Equals(s.Name, "RpcSs", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("running", rpc.State);
        Assert.Equal(rpc.Name, rpc.Value);
        Assert.False(string.IsNullOrEmpty(rpc.Detail));
    }

    [Fact]
    public async Task CredentialsAreListedByNameOnly()
    {
        var credentials = await BuiltInProviders.Devices().ListAsync("credentials", TestContext.Current.CancellationToken);

        Assert.All(credentials, c =>
        {
            Assert.DoesNotContain(WindowsCredentials.TargetPrefix, c.Name, StringComparison.Ordinal);
            Assert.Equal(c.Name, c.Value);
            Assert.Equal("Saved in Windows", c.Detail);
            Assert.Null(c.State);
        });
    }

    [Theory]
    [InlineData(1u, "stopped")]
    [InlineData(4u, "running")]
    [InlineData(7u, "paused")]
    [InlineData(2u, "changing")]
    public void ServiceStatesUseTheInspectorsWords(uint state, string words) => Assert.Equal(words, ServiceList.State(state));

    [Fact]
    public async Task UnknownKindFails() =>
        await Assert.ThrowsAsync<StepFailedException>(() => BuiltInProviders.Devices().ListAsync("toaster", TestContext.Current.CancellationToken));

    [Theory]
    [InlineData("audio")]
    [InlineData("bluetooth")]
    [InlineData("displays")]
    public async Task RealDevicesAreListedReadOnly(string kind)
    {
        var devices = await BuiltInProviders.Devices().ListAsync(kind, TestContext.Current.CancellationToken);
        Assert.All(devices, d => Assert.False(string.IsNullOrWhiteSpace(d.Name)));
        if (DevPc && kind != "bluetooth")
            Assert.NotEmpty(devices);
        if (DevPc && kind == "bluetooth")
        {
            // Settings → Bluetooth & devices on the dev PC, 2026-11-02 (D-052, X-005).
            Assert.Equal(["MX Keys Mini", "MX Master 3S", "OPPO dazewell", "Steam Ctrl (BT) FXA996140073A", "Technics EAH-AZ80", "WH-1000XM5", "Zone 301"],
                devices.Select(d => d.Detail));
            Assert.Equal([true, true, true, true, false, false, false], devices.Select(d => d.NotConnectable));
        }
        if (DevPc && kind == "audio")
            Assert.Single(devices, d => d.Detail == "Output, default");
    }

    [Fact]
    public async Task CapturedLayoutLoadsBackReadOnly()
    {
        if (!DevPc)
            return; // a CI runner may have no screen on
        var layout = await BuiltInProviders.Devices().CaptureDisplayLayoutAsync("now", TestContext.Current.CancellationToken);
        var load = ConfigYaml.Load(ConfigYaml.Snippet(layout));
        Assert.Empty(load.Problems);
        Assert.Equal(layout, Assert.Single(load.Config!.Library.Displays));
    }
}
