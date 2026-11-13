using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace CtrlAltDaze.Core.Model;

/// <summary>How Check address probes: a TCP connect, a web request, or a ping.</summary>
public enum ReachKind
{
    Tcp,
    Web,
    Ping,
}

/// <summary>What to probe. <see cref="Host"/> is the name or IP only (never a user name, password, path or query), for messages.</summary>
public sealed record ReachTarget(ReachKind Kind, string Host, int Port, Uri? Uri);

/// <summary>
/// The address of Check address (T24.8, D-140) as people write it. One place for the validator and the provider, so an
/// address the editor accepts is one the step probes: normalise first (spaces at the ends), then classify.
/// <c>http(s)://…</c> is a web address, <c>host:port</c> a TCP connect, a bare host a ping.
/// </summary>
public static class ReachAddress
{
    public const int MaxHostLength = 253;

    /// <summary>The shortest, longest and default wait for one probe.</summary>
    public static readonly TimeSpan MinTimeout = TimeSpan.FromSeconds(1), MaxTimeout = TimeSpan.FromSeconds(20), DefaultTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Reads <paramref name="text"/>; false gives a plain sentence that never repeats what was typed.</summary>
    public static bool TryParse(string? text, out ReachTarget? target, out string problem)
    {
        target = null;
        var value = (text ?? "").Trim();
        if (value.Length == 0)
            return Fail("There is no address to check. Use a host and port like nas:445, a web address, or a host name to ping.", out problem);
        if (value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '"'))
            return Fail("An address can't hold spaces, quotes or line breaks.", out problem);
        if (value.Contains("://", StringComparison.Ordinal))
            return TryWeb(value, out target, out problem);

        string host;
        string? portText = null;
        if (value[0] == '[')
        {
            var close = value.IndexOf(']', StringComparison.Ordinal);
            if (close < 0 || (close + 1 < value.Length && value[close + 1] != ':'))
                return Fail("That isn't an address. For IPv6 write [address]:port.", out problem);
            host = value[1..close];
            if (close + 1 < value.Length)
                portText = value[(close + 2)..];
            if (!IPAddress.TryParse(host, out var v6) || v6.AddressFamily != AddressFamily.InterNetworkV6 || v6.ScopeId != 0)                return Fail("That isn't an IPv6 address.", out problem);
        }
        else
        {
            var at = value.IndexOf(':', StringComparison.Ordinal);
            if (at >= 0 && value.IndexOf(':', at + 1) < 0)
            {
                host = value[..at];
                portText = value[(at + 1)..];
            }
            else
                host = value; // no colon: a name or IPv4 to ping; several: a bare IPv6 to ping
            if (!HostOk(host))
                return Fail("That isn't a host name or IP address.", out problem);
        }
        problem = "";
        if (portText is null)
        {
            target = new(ReachKind.Ping, host, 0, null);
            return true;
        }
        if (!TryPort(portText, out var port))
            return Fail("The port must be a number from 1 to 65535.", out problem);
        target = new(ReachKind.Tcp, host, port, null);
        return true;
    }

    private static bool TryWeb(string value, out ReachTarget? target, out string problem)
    {
        target = null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.Host.Length == 0)
            return Fail("That isn't a web address (http:// or https://).", out problem);
        if (uri.UserInfo.Length > 0)
            return Fail("Leave the user name and password out of the address.", out problem);
        var host = uri.Host.Trim('[', ']');
        if (!HostOk(host))
            return Fail("That isn't a web address (http:// or https://).", out problem);
        problem = "";
        target = new(ReachKind.Web, host, uri.Port, uri);
        return true;
    }

    private static bool Fail(string sentence, out string problem)
    {
        problem = sentence;
        return false;
    }

    /// <summary>A port is plain digits, 1 to 65535: no sign, spaces or separators.</summary>
    public static bool TryPort(string text, out int port) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out port) && port is >= 1 and <= 65535;

    /// <summary>An IP address (IPv4 with all four parts), or a DNS name: dot-separated labels of letters, digits, hyphens and underscores.</summary>
    private static bool HostOk(string host)
    {
        if (host.Length is 0 or > MaxHostLength)
            return false;
        if (IPAddress.TryParse(host, out var ip))
            return ip.AddressFamily == AddressFamily.InterNetworkV6 ? ip.ScopeId == 0 : host.Count(c => c == '.') == 3;
        if (host.Contains(':', StringComparison.Ordinal))
            return false;
        var name = host.TrimEnd('.');
        return name.Length > 0 && name.Split('.').All(label =>
            label.Length is > 0 and <= 63 && label[0] != '-' && label[^1] != '-' && label.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
    }

    /// <summary>
    /// The statuses that count as an answer: empty = any; <c>200</c>, <c>200-299</c> or a list like <c>200-299, 401</c>
    /// (100 to 599). Null when what is written is not that.
    /// </summary>
    public static IReadOnlyList<(int From, int To)>? TryStatuses(string? text)
    {
        var value = (text ?? "").Trim();
        if (value.Length == 0)
            return [];
        List<(int, int)> ranges = [];
        foreach (var part in value.Split(','))
        {
            var bounds = part.Split('-');
            if (bounds.Length > 2 || !TryStatus(bounds[0].Trim(), out var from) || !TryStatus(bounds[^1].Trim(), out var to) || to < from)
                return null;
            ranges.Add((from, to));
        }
        return ranges;
    }

    private static bool TryStatus(string text, out int status) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out status) && status is >= 100 and <= 599;

    /// <summary>Whether <paramref name="status"/> is inside the ranges (no ranges = any answer).</summary>
    public static bool Accepts(IReadOnlyList<(int From, int To)> ranges, int status) =>
        ranges.Count == 0 || ranges.Any(r => status >= r.From && status <= r.To);
}
