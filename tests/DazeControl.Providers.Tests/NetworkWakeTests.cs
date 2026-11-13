using System.Net;
using System.Net.Sockets;
using DazeControl.Providers.Network;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Tests;

/// <summary>T24.7 (D-139): the Wake over network step, behind a fake sink. Nothing here touches a socket.</summary>
public class NetworkWakeTests
{
    private sealed class FakeNetwork(params BroadcastTarget[] targets) : IWakeNetwork
    {
        public List<(BroadcastTarget Target, int Port, byte[] Packet)> Sent { get; } = [];
        public HashSet<string> Failing { get; } = [];

        public IReadOnlyList<BroadcastTarget> Targets() => targets;

        public Task SendAsync(BroadcastTarget target, int port, byte[] packet, CancellationToken cancellationToken)
        {
            if (Failing.Contains(target.Broadcast.ToString()))
                throw new SocketException();
            Sent.Add((target, port, packet));
            return Task.CompletedTask;
        }
    }

    private static BroadcastTarget Net(string local, string broadcast) => new(IPAddress.Parse(local), IPAddress.Parse(broadcast));

    private static readonly byte[] Mac = [0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff];

    private static Task Run(string mac, FakeNetwork network) => new NetworkWakeProvider(network).RunAsync(("mac", mac));

    [Fact]
    public async Task SendsSixFFThenTheMacSixteenTimesToPortNineOnEveryNetwork()
    {
        var network = new FakeNetwork(Net("192.168.1.5", "192.168.1.255"), Net("10.0.0.2", "10.0.0.255"));
        await Run("AA:BB:CC:DD:EE:FF", network);

        Assert.Equal(["192.168.1.255", "10.0.0.255"], network.Sent.Select(s => s.Target.Broadcast.ToString()));
        foreach (var (_, port, packet) in network.Sent)
        {
            Assert.Equal(9, port);
            Assert.Equal(102, packet.Length);
            Assert.All(packet.Take(6), b => Assert.Equal(0xFF, b));
            for (var i = 0; i < 16; i++)
                Assert.Equal(Mac, packet.Skip(6 + i * 6).Take(6));
        }
    }

    [Theory]
    [InlineData("aa:bb:cc:dd:ee:ff")]
    [InlineData("AA-BB-CC-DD-EE-FF")]
    [InlineData("aabb.ccdd.eeff")]
    [InlineData("AABBCCDDEEFF")]
    [InlineData("  aa:bb:cc:dd:ee:ff  ")]
    public async Task EveryCommonFormGivesTheSamePacket(string written)
    {
        var network = new FakeNetwork(Net("192.168.1.5", "192.168.1.255"));
        await Run(written, network);
        Assert.Equal(NetworkWakeProvider.Packet(Mac), Assert.Single(network.Sent).Packet);
    }

    [Theory]
    [InlineData("aa:bb:cc:dd:ee")]
    [InlineData("aa:bb:cc:dd:ee:ff:00")]
    [InlineData("aa:bb:cc:dd:ee:gg")]
    [InlineData("aa:bb-cc:dd-ee:ff")]
    [InlineData("aabb.ccdd.ee")]
    [InlineData("aa:bb:cc:dd:ee:f")]
    [InlineData("aa:bb:cc:dd:ee:ff\r\n11:22:33:44:55:66")]
    [InlineData("aa:bb:cc:dd:ee:ff; calc")]
    [InlineData("{x.y}")]
    public async Task ABadMacFailsWithASentenceAndSendsNothing(string written)
    {
        var network = new FakeNetwork(Net("192.168.1.5", "192.168.1.255"));
        var failure = await Assert.ThrowsAsync<StepFailedException>(() => Run(written, network));
        Assert.NotEmpty(failure.Message);
        Assert.Empty(network.Sent);
    }

    [Fact]
    public async Task NoNetworkFailsWithASentence()
    {
        var failure = await Assert.ThrowsAsync<StepFailedException>(() => Run("aa:bb:cc:dd:ee:ff", new FakeNetwork()));
        Assert.Contains("isn't connected to a network", failure.Message);
    }

    [Fact]
    public async Task OneNetworkFailingDoesNotStopTheOthers()
    {
        var network = new FakeNetwork(Net("192.168.1.5", "192.168.1.255"), Net("10.0.0.2", "10.0.0.255"));
        network.Failing.Add("192.168.1.255");
        await Run("aa:bb:cc:dd:ee:ff", network);
        Assert.Equal("10.0.0.255", Assert.Single(network.Sent).Target.Broadcast.ToString());
    }

    [Fact]
    public async Task EveryNetworkFailingFails()
    {
        var network = new FakeNetwork(Net("192.168.1.5", "192.168.1.255"));
        network.Failing.Add("192.168.1.255");
        await Assert.ThrowsAsync<StepFailedException>(() => Run("aa:bb:cc:dd:ee:ff", network));
    }

    [Fact]
    public void BroadcastIsTheAddressWithAllHostBitsSet()
    {
        Assert.Equal("192.168.1.255", WindowsWakeNetwork.BroadcastOf(IPAddress.Parse("192.168.1.77"), IPAddress.Parse("255.255.255.0"))!.ToString());
        Assert.Equal("10.255.255.255", WindowsWakeNetwork.BroadcastOf(IPAddress.Parse("10.3.4.5"), IPAddress.Parse("255.0.0.0"))!.ToString());
        Assert.Equal("172.16.3.255", WindowsWakeNetwork.BroadcastOf(IPAddress.Parse("172.16.2.9"), IPAddress.Parse("255.255.252.0"))!.ToString());
        Assert.Null(WindowsWakeNetwork.BroadcastOf(IPAddress.Parse("10.0.0.1"), IPAddress.Parse("255.255.255.255")));
    }
}
