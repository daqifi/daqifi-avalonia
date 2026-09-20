using Daqifi.Core.Device;
using Xunit;
// Core has a DaqifiStreamingDevice of its own; the app's WiFi wrapper is the one under test.
using DaqifiStreamingDevice = Daqifi.Desktop.Device.WiFiDevice.DaqifiStreamingDevice;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Pins <see cref="DaqifiStreamingDevice.CreateForManualEndpointAsync"/> — the manual-connect entry
/// point the connect dialog now calls instead of resolving the endpoint itself.
///
/// The dialog used to hold the whole transport half of "connect by IP": <c>IPAddress.TryParse</c>,
/// <c>Dns.GetHostAddressesAsync</c>, the IPv4-first preference over the answer, a
/// <c>SocketException</c> catch, and the literal wire data port <c>9760</c>. None of that is a
/// view-model's business, and none of it was covered by a test. These rows cover the paths that do
/// not need a name server, so the behaviour survives the move.
///
/// The unresolvable-host path is deliberately NOT exercised here: asserting on it means a real DNS
/// round-trip, and a resolver that answers wildcards (many consumer ISPs do) would turn a
/// non-existent name into a passing "resolved" result. That path is unchanged code, relocated.
/// </summary>
public class ManualWifiEndpointTests
{
    /// <summary>
    /// The value the dialog used to carry as its own <c>const int MANUAL_WIFI_DATA_PORT = 9760</c>.
    /// Pinned so that adopting Core's constant is visibly the same port and not a silent change —
    /// and so a Core bump that moved it would fail here rather than in the field.
    /// </summary>
    [Fact]
    public void Cores_default_tcp_data_port_is_the_9760_the_dialog_used_to_hardcode()
    {
        Assert.Equal(9760, DaqifiDeviceFactory.DefaultTcpDataPort);
    }

    [Fact]
    public async Task An_ip_literal_becomes_a_device_on_cores_default_data_port()
    {
        var device = await DaqifiStreamingDevice.CreateForManualEndpointAsync(
            "192.168.1.50", "Manual IP Device");

        Assert.NotNull(device);
        Assert.Equal("192.168.1.50", device.IpAddress);
        Assert.Equal(DaqifiDeviceFactory.DefaultTcpDataPort, device.Port);
        Assert.Equal("Manual IP Device", device.Name);
    }

    /// <summary>
    /// An IPv6 literal parses without a name lookup, exactly as it did in the dialog. The stored
    /// address is <see cref="System.Net.IPAddress"/>'s normalized form, not the typed text.
    /// </summary>
    [Fact]
    public async Task An_ipv6_literal_is_taken_as_an_address_rather_than_a_host_name()
    {
        var device = await DaqifiStreamingDevice.CreateForManualEndpointAsync(
            "0:0:0:0:0:0:0:1", "Manual IP Device");

        Assert.NotNull(device);
        Assert.Equal("::1", device.IpAddress);
    }

    /// <summary>
    /// The dialog still shows "is not a valid IP address or host name" for a syntactically invalid
    /// endpoint, so the factory has to keep throwing <see cref="ArgumentException"/> rather than
    /// returning null — the two produce different messages.
    /// </summary>
    [Fact]
    public async Task A_blank_endpoint_throws_rather_than_returning_no_device()
    {
        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => DaqifiStreamingDevice.CreateForManualEndpointAsync("   ", "Manual IP Device"));
    }

    /// <summary>
    /// The other half of that branch, and the one that reaches the resolver: a host name longer
    /// than DNS allows is rejected by <c>Dns</c> itself, synchronously and without a lookup.
    /// </summary>
    [Fact]
    public async Task An_over_long_host_name_throws_rather_than_returning_no_device()
    {
        var tooLong = new string('a', 256);

        await Assert.ThrowsAnyAsync<ArgumentException>(
            () => DaqifiStreamingDevice.CreateForManualEndpointAsync(tooLong, "Manual IP Device"));
    }
}
