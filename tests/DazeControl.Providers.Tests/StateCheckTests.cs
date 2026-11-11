using System.Net.NetworkInformation;
using DazeControl.Core.Model;
using DazeControl.Providers.Network;
using DazeControl.Providers.Pc;
using DazeControl.Runtime.Providers;
using DazeControl.Runtime.Triggers;

namespace DazeControl.Providers.Tests;

/// <summary>The D-118 checks: network, power, PC and time facts, with fakes; the real readers once against this PC.</summary>
public class StateCheckTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed class FakeNetwork(NetworkConnection[]? connections = null, NetworkAdapter[]? adapters = null, Connectivity? hint = null) : INetworkState
    {
        public IReadOnlyList<NetworkConnection> Connections() => connections ?? [];

        public IReadOnlyList<NetworkAdapter> Adapters() => adapters ?? [];

        public Connectivity? Connectivity() => hint;
    }

    private sealed class FakePc(PowerReading power = default, TimeSpan idle = default, bool? locked = false, int screens = 1, long free = 0) : IPcState
    {
        public string? AskedPath { get; private set; }

        public PowerReading Power() => power;

        public TimeSpan IdleFor() => idle;

        public bool? Locked() => locked;

        public int ScreenCount() => screens;

        public long FreeBytes(string path)
        {
            AskedPath = path;
            return free;
        }
    }

    private sealed class FixedTime(DateTime local) : TimeProvider
    {
        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

        public override DateTimeOffset GetUtcNow() => new(local, TimeSpan.Zero);
    }

    private static readonly NetworkConnection Home = new("wifi:Home", "wifi", "Home", "{a}");

    private static async Task<FactReading> Read(IFactProbe probe, string step, params (string, string)[] with) =>
        await probe.ReadAsync(Steps.Context(step, with), Token);

    [Fact]
    public async Task WifiMatchesTheNetworkByNameOrAnyWifi()
    {
        var on = new WifiProbe(new FakeNetwork([Home]));
        Assert.Equal(new FactReading(true, "On Wi-Fi Home"), await Read(on, "network.wifi", ("network", "home")));
        Assert.Equal(new FactReading(false, "On Wi-Fi Home, not Office"), await Read(on, "network.wifi", ("network", "Office")));
        Assert.True((await Read(on, "network.wifi")).Value);
        var off = new WifiProbe(new FakeNetwork([new("cable:x", "cable", "Ethernet")]));
        Assert.Equal(new FactReading(false, "Not on Wi-Fi"), await Read(off, "network.wifi", ("network", "Home")));
        Assert.False((await Read(off, "network.wifi")).Value);
    }

    [Fact]
    public async Task WifiWithAHiddenNameCantAnswerForANamedNetwork()
    {
        var hidden = new WifiProbe(new FakeNetwork([new("wifi:{a}", "wifi", "Wi-Fi", "{a}", Named: false)]));
        Assert.True((await Read(hidden, "network.wifi")).Value); // any Wi-Fi still answers
        var e = await Assert.ThrowsAsync<StepFailedException>(() => Read(hidden, "network.wifi", ("network", "Home")));
        Assert.Contains("location", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CableInternetAndMetered()
    {
        Assert.Equal(new FactReading(true, "Ethernet is connected"), await Read(new CableProbe(new FakeNetwork([new("cable:x", "cable", "Ethernet")])), "network.cable"));
        Assert.False((await Read(new CableProbe(new FakeNetwork([Home])), "network.cable")).Value);

        Assert.True((await Read(new InternetProbe(new FakeNetwork(hint: new(true, false, false))), "network.internet")).Value);
        Assert.Equal("The network wants a sign-in first", (await Read(new InternetProbe(new FakeNetwork(hint: new(false, true, false))), "network.internet")).Text);
        await Assert.ThrowsAsync<StepFailedException>(() => Read(new InternetProbe(new FakeNetwork()), "network.internet"));

        Assert.True((await Read(new MeteredProbe(new FakeNetwork(hint: new(true, false, true))), "network.metered")).Value);
        Assert.False((await Read(new MeteredProbe(new FakeNetwork(hint: new(true, false, false))), "network.metered")).Value);
    }

    [Fact]
    public async Task VpnByKnownWordsOrByName()
    {
        NetworkAdapter[] adapters =
        [
            new("Ethernet", "Intel(R) Ethernet", NetworkInterfaceType.Ethernet),
            new("Teredo", "Teredo Tunneling Pseudo-Interface", NetworkInterfaceType.Tunnel),
        ];
        Assert.False((await Read(new VpnProbe(new FakeNetwork(adapters: adapters)), "network.vpn")).Value);
        var wg = new VpnProbe(new FakeNetwork(adapters: [.. adapters, new("Work", "WireGuard Tunnel", NetworkInterfaceType.Unknown)]));
        Assert.Equal(new FactReading(true, "A VPN is connected"), await Read(wg, "network.vpn"));
        Assert.True((await Read(wg, "network.vpn", ("name", "work"))).Value);
        Assert.Equal(new FactReading(false, "Office isn't connected"), await Read(wg, "network.vpn", ("name", "Office")));
        Assert.True(VpnProbe.IsVpn(new("Dial", "WAN Miniport", NetworkInterfaceType.Ppp)));
    }

    [Fact]
    public async Task PowerAndBattery()
    {
        Assert.Equal(new FactReading(true, "Plugged in"), await Read(new PluggedInProbe(new FakePc(new(true, 80))), "power.source"));
        Assert.Equal(new FactReading(false, "On battery"), await Read(new PluggedInProbe(new FakePc(new(false, 80))), "power.source"));
        await Assert.ThrowsAsync<StepFailedException>(() => Read(new PluggedInProbe(new FakePc(new(null, null))), "power.source"));

        var battery = new BatteryProbe(new FakePc(new(false, 35)));
        Assert.Equal(new FactReading(true, "The battery is at 35%"), await Read(battery, "power.battery", ("percent", "35")));
        Assert.False((await Read(battery, "power.battery", ("percent", "36"))).Value);
        Assert.True((await Read(battery, "power.battery")).Value); // default 20 %
        await Assert.ThrowsAsync<StepFailedException>(() => Read(new BatteryProbe(new FakePc(new(true, null))), "power.battery"));
    }

    [Fact]
    public async Task IdleLockedAndScreens()
    {
        var idle = new IdleProbe(new FakePc(idle: TimeSpan.FromSeconds(390.6)));
        Assert.Equal(new FactReading(true, "No input for 6m30s"), await Read(idle, "pc.idle")); // default 5m
        Assert.Equal(new FactReading(false, "Last input 6m30s ago"), await Read(idle, "pc.idle", ("for", "10m")));
        Assert.Equal("In use right now", (await Read(new IdleProbe(new FakePc()), "pc.idle")).Text);

        Assert.True((await Read(new LockedProbe(new FakePc(locked: true)), "pc.locked")).Value);
        Assert.False((await Read(new LockedProbe(new FakePc(locked: false)), "pc.locked")).Value);
        await Assert.ThrowsAsync<StepFailedException>(() => Read(new LockedProbe(new FakePc(locked: null)), "pc.locked"));

        Assert.Equal(new FactReading(true, "3 screens are on"), await Read(new ScreenCountProbe(new FakePc(screens: 3)), "display.count"));
        Assert.Equal(new FactReading(false, "1 screen is on"), await Read(new ScreenCountProbe(new FakePc(screens: 1)), "display.count"));
    }

    [Fact]
    public async Task FreeSpaceInGigabytesOnTheDrive()
    {
        var pc = new FakePc(free: 12_400_000_000);
        Assert.Equal(new FactReading(true, "D: has 12 GB free"), await Read(new FreeSpaceProbe(pc), "file.space", ("drive", "D:")));
        Assert.Equal("D:\\", pc.AskedPath);
        Assert.False((await Read(new FreeSpaceProbe(pc), "file.space", ("drive", "D:"), ("gb", "20"))).Value);
        Assert.Equal("C: has 0.5 GB free", (await Read(new FreeSpaceProbe(new FakePc(free: 500_000_000)), "file.space")).Text);
    }

    [Theory]
    [InlineData("2026-11-11 08:00", "weekdays", "07:00", "09:00", true)] // Wednesday morning
    [InlineData("2026-11-11 09:00", "weekdays", "07:00", "09:00", false)] // To is not included
    [InlineData("2026-11-14 08:00", "weekdays", "07:00", "09:00", false)] // Saturday
    [InlineData("2026-11-14 08:00", "weekends", null, null, true)] // all day
    [InlineData("2026-11-13 23:30", "weekdays", "22:00", "06:00", true)] // Friday night
    [InlineData("2026-11-14 05:00", "weekdays", "22:00", "06:00", true)] // Saturday early = Friday's night
    [InlineData("2026-11-15 05:00", "weekdays", "22:00", "06:00", false)] // Sunday early = Saturday's night
    [InlineData("2026-11-11 12:00", "daily", "22:00", "06:00", false)]
    public async Task TimeWindow(string now, string days, string? from, string? to, bool inside)
    {
        var time = new FixedTime(DateTime.Parse(now, System.Globalization.CultureInfo.InvariantCulture));
        List<(string, string)> with = [("days", days)];
        if (from is not null)
            with.AddRange([("from", from), ("to", to!)]);
        Assert.Equal(inside, (await Read(new TimeWindowProbe(time), "time.check", [.. with])).Value);
    }

    [Fact]
    public async Task CheckStepsFailWithTheReadingWhenTheFactIsntAsChosen()
    {
        var check = new FactCheckProvider("power.source", new PluggedInProbe(new FakePc(new(false, 50))), "pluggedIn");
        var e = await Assert.ThrowsAsync<StepFailedException>(() => check.ExecuteAsync(Steps.Context("power.source"), Token));
        Assert.Equal("On battery", e.Message);
        await check.ExecuteAsync(Steps.Context("power.source", ("is", "onBattery")), Token);
    }

    [Fact]
    public void RealReadersAnswerOnThisPc()
    {
        // Values depend on the PC; this only proves the native reads work without admin and don't throw.
        var pc = new WindowsPcState();
        _ = pc.Power();
        Assert.True(pc.IdleFor() >= TimeSpan.Zero);
        _ = pc.Locked();
        Assert.True(pc.ScreenCount() >= 0);
        Assert.True(pc.FreeBytes(Path.GetTempPath()) > 0);
        var network = new WindowsNetworkState();
        _ = network.Connections();
        _ = network.Adapters();
        _ = network.Connectivity(); // level/struct layout checked against the raw API on 2026-11-11: online = 3, 12 bytes written
        _ = WindowsNetworkState.WifiNetworks();
    }
}
