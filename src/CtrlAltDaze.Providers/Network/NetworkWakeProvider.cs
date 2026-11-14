using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using CtrlAltDaze.Core.Model;
using CtrlAltDaze.Runtime.Providers;

namespace CtrlAltDaze.Providers.Network;

/// <summary>A connected IPv4 network: the address this PC has on it and the address that reaches every device on it.</summary>
public readonly record struct BroadcastTarget(IPAddress Local, IPAddress Broadcast);

/// <summary>The networks to wake on and the sending itself, behind a seam so tests send nothing (D-139).</summary>
public interface IWakeNetwork
{
    /// <summary>Each connected IPv4 network, read now.</summary>
    IReadOnlyList<BroadcastTarget> Targets();

    /// <summary>Sends <paramref name="packet"/> as one UDP broadcast on that network, then closes the socket.</summary>
    Task SendAsync(BroadcastTarget target, int port, byte[] packet, CancellationToken cancellationToken);
}

/// <summary>Reads the adapters and sends from a socket bound to the network's own address; nothing stays open or resident (D-009).</summary>
public sealed class WindowsWakeNetwork : IWakeNetwork
{
    public IReadOnlyList<BroadcastTarget> Targets()
    {
        List<BroadcastTarget> targets = [];
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up
                || adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                continue;
            try
            {
                foreach (var unicast in adapter.GetIPProperties().UnicastAddresses)
                {
                    var address = unicast.Address;
                    // Link-local (169.254.x.x) = no real network was found; a /32 has nothing to broadcast to.
                    if (address.AddressFamily != AddressFamily.InterNetwork || address.GetAddressBytes() is [169, 254, ..]
                        || unicast.IPv4Mask is not { } mask || BroadcastOf(address, mask) is not { } broadcast)
                        continue;
                    targets.Add(new BroadcastTarget(address, broadcast));
                }
            }
            catch (NetworkInformationException)
            {
                // The adapter went away while being read: not a network.
            }
        }
        return targets;
    }

    /// <summary>The address with every host bit set; null for a /31 or /32 (no broadcast address).</summary>
    public static IPAddress? BroadcastOf(IPAddress address, IPAddress mask)
    {
        var a = address.GetAddressBytes();
        var m = mask.GetAddressBytes();
        // A /31 link has no broadcast (the all-ones address is the peer host) and a /32 has nobody to reach: two host bits at least.
        if (a.Length != 4 || m.Length != 4 || m.Sum(b => System.Numerics.BitOperations.PopCount(b)) > 30)
            return null;
        return new IPAddress(a.Select((b, i) => (byte)(b | ~m[i])).ToArray());
    }

    public async Task SendAsync(BroadcastTarget target, int port, byte[] packet, CancellationToken cancellationToken)
    {
        using var client = new UdpClient(new IPEndPoint(target.Local, 0)) { EnableBroadcast = true };
        await client.SendAsync(packet, new IPEndPoint(target.Broadcast, port), cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// <c>network.wake</c> (T24.7, D-139): a Wake-on-LAN magic packet (six 0xFF bytes, then the MAC sixteen times) as a UDP
/// broadcast to port 9 on every connected IPv4 network. The MAC is normalised, then checked, after values are filled (D-129):
/// a value is data, and what is not a MAC fails the step with a sentence. Sockets live only for the send (idle = zero).
/// </summary>
public sealed class NetworkWakeProvider(IWakeNetwork network) : IStepProvider
{
    public const int Port = 9;

    public string Id => "network.wake";

    public async Task ExecuteAsync(StepContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var text = context.RequiredText("mac");
        if (!MacAddress.TryParse(text, out var mac))
            throw new StepFailedException(text.Trim().Length == 0
                ? "There is no hardware address to wake."
                : "That isn't a hardware address. Use six pairs of digits, like aa:bb:cc:dd:ee:ff.");
        cancellationToken.ThrowIfCancellationRequested();
        var targets = network.Targets();
        if (targets.Count == 0)
            throw new StepFailedException("This PC isn't connected to a network, so there is nothing to send the wake-up on.");
        var packet = Packet(mac);
        var sent = 0;
        Exception? last = null;
        foreach (var target in targets)
        {
            try
            {
                await network.SendAsync(target, Port, packet, cancellationToken).ConfigureAwait(false);
                sent++;
            }
            catch (Exception e) when (e is SocketException or InvalidOperationException or IOException)
            {
                last = e; // one network failing must not stop the others
            }
        }
        if (sent == 0)
            throw new StepFailedException($"Couldn't send the wake-up: {last?.Message}", last!);
        context.SetSummary($"Sent the wake-up for {MacAddress.Format(mac)} on {sent} network{(sent == 1 ? "" : "s")}");
    }

    /// <summary>Six 0xFF bytes, then the MAC sixteen times (102 bytes).</summary>
    public static byte[] Packet(byte[] mac)
    {
        var packet = new byte[6 + 16 * 6];
        Array.Fill(packet, (byte)0xFF, 0, 6);
        for (var i = 0; i < 16; i++)
            mac.CopyTo(packet, 6 + i * 6);
        return packet;
    }
}
