using System.Net;
using System.Net.Sockets;

namespace Daqifi.Avalonia.Services;

/// <summary>A DAQiFi device found by <see cref="NativeWiFiDiscovery"/>.</summary>
public sealed record DiscoveredDevice(IPAddress Ip, int Port, string? Name);

/// <summary>
/// Self-contained UDP broadcast discovery for the mobile heads.
///
/// The ported Core <c>WiFiDeviceFinder</c> works on desktop but finds
/// nothing on Android: its NIC-enumerated <c>255.255.255.255</c> (limited)
/// broadcast is dropped by Android/WiFi hardware. This sends the DAQiFi
/// finder queries to the SUBNET-DIRECTED broadcast (e.g. 192.168.1.255,
/// computed from the phone's own WiFi address) — which Android delivers —
/// plus limited broadcast as a fallback, then takes each responder's IP
/// straight from the reply packet's source address (no protobuf parse
/// needed; the SN + firmware come back on connect). The caller holds the
/// WiFi MulticastLock (<see cref="NetworkDiscoveryScope"/>) for the sweep.
/// </summary>
public static class NativeWiFiDiscovery
{
    private const int DiscoveryPort = 30303;
    private const int DefaultDataPort = 9760;
    private static readonly byte[] DaqifiQuery = "DAQiFi?\r\n"u8.ToArray();
    private static readonly byte[] NativeQuery = "Discovery: Who is out there?\r\n"u8.ToArray();

    /// <summary>
    /// Broadcast, collect responders until <paramref name="window"/>
    /// elapses, and report each unique device via <paramref name="onFound"/>
    /// (invoked on a background thread — the caller marshals to the UI).
    /// </summary>
    public static async Task DiscoverAsync(
        TimeSpan window, Action<DiscoveredDevice> onFound, CancellationToken token)
    {
        using var sock = new Socket(AddressFamily.InterNetwork,
            SocketType.Dgram, ProtocolType.Udp);
        sock.EnableBroadcast = true;
        sock.SetSocketOption(SocketOptionLevel.Socket,
            SocketOptionName.ReuseAddress, true);
        sock.Bind(new IPEndPoint(IPAddress.Any, 0));

        foreach (var target in BroadcastTargets())
        {
            var ep = new IPEndPoint(target, DiscoveryPort);
            try
            {
                await sock.SendToAsync(DaqifiQuery, SocketFlags.None, ep, token);
                await sock.SendToAsync(NativeQuery, SocketFlags.None, ep, token);
            }
            catch (SocketException) { /* one target unreachable — try the rest */ }
        }

        var seen = new HashSet<string>();
        var buffer = new byte[2048];
        var deadline = DateTime.UtcNow + window;
        while (!token.IsCancellationRequested)
        {
            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero) { break; }
            var from = new IPEndPoint(IPAddress.Any, 0);
            SocketReceiveFromResult result;
            try
            {
                using var timeoutCts = CancellationTokenSource
                    .CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(remaining);
                result = await sock.ReceiveFromAsync(
                    buffer, SocketFlags.None, from, timeoutCts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) { continue; }

            if (result.ReceivedBytes <= 0) { continue; }
            var ip = ((IPEndPoint)result.RemoteEndPoint).Address;
            if (!seen.Add(ip.ToString())) { continue; }
            onFound(new DiscoveredDevice(ip, DefaultDataPort, $"DAQiFi @ {ip}"));
        }
    }

    /// <summary>
    /// Subnet-directed broadcast(s) for every up IPv4 interface, plus the
    /// limited-broadcast fallback. Subnet-directed is what Android actually
    /// delivers; limited broadcast covers hosts that only answer it.
    /// </summary>
    private static IEnumerable<IPAddress> BroadcastTargets()
    {
        var targets = new List<IPAddress>();
        foreach (var (addr, mask) in LocalIPv4Interfaces())
        {
            var a = addr.GetAddressBytes();
            var m = mask.GetAddressBytes();
            var b = new byte[4];
            for (var i = 0; i < 4; i++) { b[i] = (byte)(a[i] | (~m[i] & 0xFF)); }
            var bcast = new IPAddress(b);
            if (!targets.Contains(bcast)) { targets.Add(bcast); }
        }
        targets.Add(IPAddress.Broadcast);   // 255.255.255.255 fallback
        return targets;
    }

    private static IEnumerable<(IPAddress Addr, IPAddress Mask)> LocalIPv4Interfaces()
    {
        foreach (var nic in System.Net.NetworkInformation.NetworkInterface
                     .GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != System.Net.NetworkInformation
                    .OperationalStatus.Up)
            {
                continue;
            }
            foreach (var ua in nic.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork) { continue; }
                if (IPAddress.IsLoopback(ua.Address)) { continue; }
                var mask = ua.IPv4Mask;
                if (mask == null || Equals(mask, IPAddress.Any))
                {
                    mask = IPAddress.Parse("255.255.255.0"); // sane /24 default
                }
                yield return (ua.Address, mask);
            }
        }
    }
}
