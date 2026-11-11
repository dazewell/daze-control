using System.Globalization;
using System.Net.NetworkInformation;
using DazeControl.Core.Model;
using DazeControl.Providers.Network;
using DazeControl.Runtime.Providers;

namespace DazeControl.Providers.Pc;

// The PC-state probes behind the D-118 checks. Each reads once when its step runs (D-081); a probe that can't
// answer throws, so a check fails with that sentence instead of guessing.

/// <summary><c>network.wifi</c>: on Wi-Fi at all, or on the network named in <c>network</c>.</summary>
public sealed class WifiProbe(INetworkState network) : IFactProbe
{
    public string Fact => "network.wifi";

    public Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken)
    {
        var wanted = context.Text("network")?.Trim();
        var wifi = network.Connections().Where(c => c.Kind == "wifi").ToList();
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(wanted))
        {
            var named = wifi.Where(c => c.Named).Select(c => c.Detail).ToList();
            return Task.FromResult(wifi.Count == 0 ? new FactReading(false, "Not on Wi-Fi")
                : new FactReading(true, named.Count > 0 ? $"On Wi-Fi {string.Join(", ", named)}" : "On Wi-Fi"));
        }
        if (wifi.FirstOrDefault(c => c.Named && string.Equals(c.Detail, wanted, StringComparison.OrdinalIgnoreCase)) is { } match)
            return Task.FromResult(new FactReading(true, $"On Wi-Fi {match.Detail}"));
        if (wifi.Count == 0)
            return Task.FromResult(new FactReading(false, "Not on Wi-Fi"));
        if (wifi.FirstOrDefault(c => c.Named) is { } other)
            return Task.FromResult(new FactReading(false, $"On Wi-Fi {other.Detail}, not {wanted}"));
        // Windows 11 hides network names from apps without location access.
        throw new StepFailedException("Windows didn't say which Wi-Fi network this is. Allow location for desktop apps in Settings › Privacy & security › Location.");
    }
}

/// <summary><c>network.cable</c>: a network cable (an Ethernet-type adapter with a gateway) is connected.</summary>
public sealed class CableProbe(INetworkState network) : IFactProbe
{
    public string Fact => "network.cable";

    public Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken)
    {
        var cable = network.Connections().FirstOrDefault(c => c.Kind == "cable");
        return Task.FromResult(cable is null ? new FactReading(false, "No network cable is connected") : new FactReading(true, $"{cable.Detail} is connected"));
    }
}

/// <summary><c>network.internet</c>: Windows' own connectivity check says the internet is reachable.</summary>
public sealed class InternetProbe(INetworkState network) : IFactProbe
{
    public string Fact => "network.internet";

    public Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken)
    {
        var hint = network.Connectivity() ?? throw new StepFailedException("Windows doesn't know yet whether the internet is reachable.");
        return Task.FromResult(hint.Internet ? new FactReading(true, "The internet is reachable")
            : new FactReading(false, hint.Captive ? "The network wants a sign-in first" : "No internet"));
    }
}

/// <summary><c>network.metered</c>: the connection Windows uses counts as metered (or is roaming or over its data limit).</summary>
public sealed class MeteredProbe(INetworkState network) : IFactProbe
{
    public string Fact => "network.metered";

    public Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken)
    {
        var hint = network.Connectivity() ?? throw new StepFailedException("Windows doesn't know whether the connection is metered.");
        return Task.FromResult(hint.Metered ? new FactReading(true, "The connection is metered") : new FactReading(false, "The connection isn't metered"));
    }
}

/// <summary>
/// <c>network.vpn</c>: a VPN adapter is up. With <c>name</c>: an adapter whose name or description holds it; without, a
/// PPP adapter or one whose description names a known VPN (tunnels Windows adds by itself, like Teredo, don't count).
/// </summary>
public sealed class VpnProbe(INetworkState network) : IFactProbe
{
    private static readonly string[] Words =
    [
        "vpn", "wireguard", "wintun", "openvpn", "tap-windows", "tailscale", "zerotier", "anyconnect", "globalprotect", "pangp",
        "fortinet", "fortissl", "juniper", "pulse secure", "nordlynx", "expressvpn", "protonvpn", "mullvad", "sonicwall", "checkpoint",
        "l2tp", "pptp", "sstp", "ikev2",
    ];

    public string Fact => "network.vpn";

    public Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken)
    {
        var name = context.Text("name")?.Trim();
        var adapters = network.Adapters();
        var found = string.IsNullOrEmpty(name)
            ? adapters.FirstOrDefault(IsVpn)
            : adapters.FirstOrDefault(a => Has(a.Name, name) || Has(a.Description, name));
        var label = string.IsNullOrEmpty(name) ? "A VPN" : name;
        return Task.FromResult(found == default ? new FactReading(false, $"{(string.IsNullOrEmpty(name) ? "No VPN" : name)} isn't connected")
            : new FactReading(true, $"{label} is connected"));
    }

    internal static bool IsVpn(NetworkAdapter adapter) =>
        adapter.Type == NetworkInterfaceType.Ppp || Words.Any(w => Has(adapter.Description, w) || Has(adapter.Name, w));

    private static bool Has(string text, string part) => text.Contains(part, StringComparison.OrdinalIgnoreCase);
}

/// <summary><c>power.ac</c>: the PC is plugged in.</summary>
public sealed class PluggedInProbe(IPcState pc) : IFactProbe
{
    public string Fact => "power.ac";

    public Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken) =>
        Task.FromResult(pc.Power().PluggedIn switch
        {
            true => new FactReading(true, "Plugged in"),
            false => new FactReading(false, "On battery"),
            null => throw new StepFailedException("Windows didn't say whether the PC is plugged in."),
        });
}

/// <summary><c>power.battery</c>: the battery is at <c>percent</c> or more.</summary>
public sealed class BatteryProbe(IPcState pc) : IFactProbe
{
    public string Fact => "power.battery";

    public Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken)
    {
        var limit = context.WholeNumber("percent", 20);
        var percent = pc.Power().BatteryPercent ?? throw new StepFailedException("This PC has no battery, or Windows didn't say how full it is.");
        return Task.FromResult(new FactReading(percent >= limit, $"The battery is at {percent}%"));
    }
}

/// <summary><c>pc.idle</c>: no keyboard or mouse input for <c>for</c> or longer.</summary>
public sealed class IdleProbe(IPcState pc) : IFactProbe
{
    public string Fact => "pc.idle";

    public Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken)
    {
        var wanted = context.Duration("for");
        var idle = pc.IdleFor();
        return Task.FromResult(idle >= wanted
            ? new FactReading(true, $"No input for {Duration.Format(Whole(idle))}")
            : new FactReading(false, idle < TimeSpan.FromSeconds(1) ? "In use right now" : $"Last input {Duration.Format(Whole(idle))} ago"));
    }

    private static TimeSpan Whole(TimeSpan span) => TimeSpan.FromSeconds(Math.Floor(span.TotalSeconds));
}

/// <summary><c>pc.locked</c>: this session is locked.</summary>
public sealed class LockedProbe(IPcState pc) : IFactProbe
{
    public string Fact => "pc.locked";

    public Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken) =>
        Task.FromResult(pc.Locked() switch
        {
            true => new FactReading(true, "The PC is locked"),
            false => new FactReading(false, "The PC isn't locked"),
            null => throw new StepFailedException("Windows didn't say whether the PC is locked."),
        });
}

/// <summary><c>display.count</c>: at least <c>count</c> screens show the desktop.</summary>
public sealed class ScreenCountProbe(IPcState pc) : IFactProbe
{
    public string Fact => "display.count";

    public Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken)
    {
        var wanted = context.WholeNumber("count", 2);
        var count = pc.ScreenCount();
        return Task.FromResult(new FactReading(count >= wanted, count == 1 ? "1 screen is on" : $"{count} screens are on"));
    }
}

/// <summary><c>file.space</c>: the drive of <c>drive</c> has at least <c>gb</c> GB free.</summary>
public sealed class FreeSpaceProbe(IPcState pc) : IFactProbe
{
    public string Fact => "file.space";

    public Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken)
    {
        var path = Environment.ExpandEnvironmentVariables(context.Text("drive") is { Length: > 0 } text ? text.Trim() : "C:\\");
        if (path.Length == 2 && path[1] == ':')
            path += "\\"; // "D:" alone means the current folder on D:
        var gb = context.WholeNumber("gb", 10);
        long free;
        try
        {
            free = pc.FreeBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new StepFailedException($"Can't read the free space on {path}: {e.Message}");
        }
        var freeGb = free / 1_000_000_000d;
        var drive = Path.GetPathRoot(path)?.TrimEnd('\\') is { Length: > 0 } root ? root : path;
        return Task.FromResult(new FactReading(freeGb >= gb, $"{drive} has {freeGb.ToString(freeGb < 10 ? "0.#" : "0", CultureInfo.InvariantCulture)} GB free"));
    }
}

/// <summary>
/// <c>time.window</c>: now is on one of <c>days</c> and between <c>from</c> and <c>to</c> (local time). A window that ends
/// before it starts runs past midnight (22:00 to 06:00); its morning part belongs to the day it started. No times = all day.
/// </summary>
public sealed class TimeWindowProbe(TimeProvider time) : IFactProbe
{
    public string Fact => "time.window";

    public Task<FactReading> ReadAsync(StepContext context, CancellationToken cancellationToken)
    {
        var now = time.GetLocalNow().DateTime;
        if (!TriggerText.TryParseDays(context.Text("days") ?? "daily", out var days))
            throw new StepFailedException("Choose the days.");
        var hasFrom = TriggerText.TryParseTime(context.Text("from"), out var from);
        var hasTo = TriggerText.TryParseTime(context.Text("to"), out var to);
        var inside = Inside(now, days, hasFrom ? from : null, hasTo ? to : null);
        var clock = now.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
        return Task.FromResult(new FactReading(inside, inside ? $"{clock} is in the time window" : $"{clock} is outside the time window"));
    }

    internal static bool Inside(DateTime now, Days days, TimeOnly? from, TimeOnly? to)
    {
        var clock = TimeOnly.FromDateTime(now);
        if (from is not { } start || to is not { } end)
            return OnDay(now, days);
        if (start < end)
            return clock >= start && clock < end && OnDay(now, days);
        // Past midnight: the evening part is today's, the morning part yesterday's.
        return clock >= start ? OnDay(now, days) : clock < end && OnDay(now.AddDays(-1), days);
    }

    private static bool OnDay(DateTime when, Days days) => (days & Day(when.DayOfWeek)) != 0;

    private static Days Day(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => Days.Mon,
        DayOfWeek.Tuesday => Days.Tue,
        DayOfWeek.Wednesday => Days.Wed,
        DayOfWeek.Thursday => Days.Thu,
        DayOfWeek.Friday => Days.Fri,
        DayOfWeek.Saturday => Days.Sat,
        _ => Days.Sun,
    };
}
