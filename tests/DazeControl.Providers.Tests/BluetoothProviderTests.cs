using DazeControl.Core.Model;
using DazeControl.Providers.Bluetooth;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Tests;

public class BluetoothProviderTests
{
    private static readonly BluetoothDevice Technics = new("technics", "B8:20:8E:ED:48:FC", "Technics EAH-AZ80");

    private sealed class FakeBluetooth : IBluetoothAudio
    {
        public List<string> Calls { get; } = [];

        public Task<BluetoothChange> ConnectAsync(BluetoothDevice device, CancellationToken cancellationToken)
        {
            Calls.Add($"connect {device.Address}");
            return Task.FromResult(BluetoothChange.Done);
        }

        public Task<BluetoothChange> DisconnectAsync(BluetoothDevice device, CancellationToken cancellationToken)
        {
            Calls.Add($"disconnect {device.Address}");
            return Task.FromResult(BluetoothChange.Done);
        }
    }

    private static Task Run(IStepProvider provider, string device) =>
        provider.ExecuteAsync(Steps.Context(provider.Id, ("device", device)) with
        {
            Library = new Library { Bluetooth = [Technics] },
        }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task ConnectResolvesTheLibraryDevice()
    {
        var bluetooth = new FakeBluetooth();
        await Run(new BluetoothConnectProvider(bluetooth), "technics");
        await Run(new BluetoothDisconnectProvider(bluetooth), "technics");
        Assert.Equal(["connect B8:20:8E:ED:48:FC", "disconnect B8:20:8E:ED:48:FC"], bluetooth.Calls);
    }

    [Fact]
    public async Task UnknownDeviceFailsPlainly()
    {
        var failure = await Assert.ThrowsAsync<StepFailedException>(() => Run(new BluetoothConnectProvider(new FakeBluetooth()), "sony"));
        Assert.Equal("No Bluetooth device called \"sony\" in the library.", failure.Message);
    }

    [Theory]
    [InlineData("B8:20:8E:ED:48:FC")]
    [InlineData("b8-20-8e-ed-48-fc")]
    [InlineData("B8208EED48FC")]
    public void AddressBecomesTheDevicePathForm(string address) => Assert.Equal("B8208EED48FC", KsBluetoothAudio.Hex(address));

    [Fact]
    public async Task UnpairedAddressFailsWithoutSendingAnything()
    {
        var failure = await Assert.ThrowsAsync<StepFailedException>(() => new KsBluetoothAudio().ConnectAsync(
            new BluetoothDevice("ghost", "00:11:22:33:44:55", "Ghost"), TestContext.Current.CancellationToken));
        Assert.Equal("Ghost isn't paired with this PC as an audio device.", failure.Message);
    }

    [Fact]
    public async Task FindsThePairedTechnicsEndpointsReadOnly()
    {
        // Dev PC only (the user's Technics is paired there); elsewhere the device simply has no endpoints.
        var (total, active) = await KsBluetoothAudio.InspectAsync(Technics, TestContext.Current.CancellationToken);
        Assert.InRange(active, 0, total);
        if (Environment.GetEnvironmentVariable("DAZE_DEV_PC") == "1")
            Assert.True(total >= 2, $"{total} endpoints"); // A2DP + hands-free at least
    }
}
