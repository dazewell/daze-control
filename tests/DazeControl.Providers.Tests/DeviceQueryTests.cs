using DazeControl.Core.Yaml;
using DazeControl.Providers.Bluetooth;
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
            Assert.Contains(devices, d => d.Name == "B8:20:8E:ED:48:FC"); // the user's Technics
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
