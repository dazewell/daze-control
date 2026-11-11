using DazeControl.Core.Catalog;
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
    public async Task ABoundDeviceConnectsItsBluetoothSide()
    {
        var bluetooth = new FakeBluetooth();
        var provider = new BluetoothConnectProvider(bluetooth);
        var context = Steps.Context(provider.Id, ("device", "headphones")) with
        {
            Library = new Library { Bluetooth = [Technics], Bound = [new BoundDevice("headphones", "technics", "gone")] },
        };

        await provider.ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(["connect B8:20:8E:ED:48:FC"], bluetooth.Calls); // the missing audio side doesn't matter here
    }

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
        Assert.Equal("There's no Bluetooth device called \"sony\" in your Library.", failure.Message);
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

    private sealed class FakeState(bool? connected) : IBluetoothState
    {
        public Task<bool?> IsConnectedAsync(BluetoothDevice device, CancellationToken cancellationToken) =>
            Task.FromResult(connected);
    }

    private static Task<FactReading> Read(bool? connected) =>
        new BluetoothConnectedProbe(new FakeState(connected)).ReadAsync(
            Steps.Context("bluetooth.connect", ("device", "technics")) with { Library = new Library { Bluetooth = [Technics] } },
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task TheProbeSaysInWordsWhetherTheDeviceIsConnected()
    {
        Assert.Equal(new FactReading(true, "Technics EAH-AZ80 is connected"), await Read(true));
        Assert.Equal(new FactReading(false, "Technics EAH-AZ80 isn't connected"), await Read(false));
    }

    [Fact]
    public async Task TheProbeThrowsWhenWindowsDoesNotSay() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() => Read(null));

    [Theory]
    [InlineData(true, null, true)]
    [InlineData(false, null, false)]
    [InlineData(true, "disconnected", false)]
    [InlineData(false, "disconnected", true)]
    public async Task TheCheckFailsWhenTheDeviceIsNotAsChosen(bool connected, string? @is, bool passes)
    {
        var check = new BluetoothCheckProvider(new BluetoothConnectedProbe(new FakeState(connected)));
        var context = Steps.Context("bluetooth.check", @is is null ? [("device", "technics")] : [("device", "technics"), ("is", @is)]) with
        {
            Library = new Library { Bluetooth = [Technics] },
        };
        var run = check.ExecuteAsync(context, TestContext.Current.CancellationToken);
        if (passes)
            await run;
        else
            Assert.Equal(connected ? "Technics EAH-AZ80 is connected." : "Technics EAH-AZ80 isn't connected.",
                (await Assert.ThrowsAsync<StepFailedException>(() => run)).Message);
    }

    [Fact]
    public void ConnectAndDisconnectOfferOnlyIfOnTheSameFact()
    {
        Assert.Equal(("disconnected", false), (ProviderCatalog.Find("bluetooth.connect")!.Conditions[0].Id, ProviderCatalog.Find("bluetooth.connect")!.Conditions[0].Expected));
        Assert.Equal(("connected", true), (ProviderCatalog.Find("bluetooth.disconnect")!.Conditions[0].Id, ProviderCatalog.Find("bluetooth.disconnect")!.Conditions[0].Expected));
        Assert.All(["bluetooth.connect", "bluetooth.disconnect"], id =>
            Assert.Equal("bluetooth.connected", ProviderCatalog.Find(id)!.Conditions[0].Fact));
    }
}
