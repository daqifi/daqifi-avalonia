using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Desktop.Device;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Pins where <see cref="AbstractStreamingDevice.HasWincWifiModule"/> gets its answer.
///
/// This one boolean decides whether the app probes and offers to flash the device's WiFi
/// firmware: it gates the FLASH WIFI button, the automatic post-connect WiFi-firmware check
/// (which sends <c>SYSTem:COMMunicate:LAN:GETChipInfo?</c>), and the WiFi-version row on the
/// device tile. Answering it wrong in the permissive direction means running a WINC-specific
/// probe against a device that has no WINC module, where that query returns non-version data.
///
/// It used to be answered by a board list compiled into this app
/// (<c>DeviceType is Nyquist1 or Nyquist2 or Nyquist3</c>). It is now answered by
/// <see cref="DeviceCapabilities.HasWincWifiModule"/>, which Core maintains and merges with what
/// the connected device reports about itself. The two agree on every board that exists today, so
/// nothing but a test can hold the wiring in place — these cases are written so that each one
/// fails against the old expression.
/// </summary>
public class AbstractStreamingDeviceWincGateTests
{
    /// <summary>
    /// Minimal concrete <see cref="AbstractStreamingDevice"/>. The base class needs three members
    /// implemented and none of them is exercised here: the gate under test is a pure read of
    /// <see cref="AbstractStreamingDevice.Metadata"/>, and no test in this class connects, writes
    /// or sends anything.
    /// </summary>
    private sealed class TestDevice : AbstractStreamingDevice
    {
        public override ConnectionType ConnectionType => ConnectionType.Usb;

        public override bool Write(string command) => throw new NotSupportedException();

        protected override void SendMessage(IOutboundMessage<string> message) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void Gate_is_closed_on_a_device_that_has_not_described_itself_yet()
    {
        var device = new TestDevice();

        // A fresh DeviceCapabilities is all-false, which per Core's ADR 0001 means "not yet
        // known" rather than "hardware absent" — either way the WINC probe must not run.
        Assert.False(device.HasWincWifiModule);
    }

    [Fact]
    public void Gate_follows_Core_capability_even_when_the_board_is_not_recognised()
    {
        var device = new TestDevice
        {
            DeviceType = DeviceType.Unknown,
        };
        device.Metadata.Capabilities = new DeviceCapabilities { HasWincWifiModule = true };

        // The deleted expression returned false here, because Unknown is not on its board list.
        Assert.True(device.HasWincWifiModule);
    }

    [Fact]
    public void Gate_follows_Core_capability_when_a_known_board_reports_no_WINC_module()
    {
        var device = new TestDevice
        {
            DeviceType = DeviceType.Nyquist3,
        };
        device.Metadata.Capabilities = new DeviceCapabilities { HasWincWifiModule = false };

        // The deleted expression returned true here purely because the board is a Nyquist —
        // the exact "compiled-in guess overrules the device" shape this change removes.
        Assert.False(device.HasWincWifiModule);
    }

    [Theory]
    [InlineData(DeviceType.Nyquist1)]
    [InlineData(DeviceType.Nyquist2)]
    [InlineData(DeviceType.Nyquist3)]
    public void Gate_still_opens_for_the_boards_that_do_carry_a_WINC_module(DeviceType board)
    {
        var device = new TestDevice { DeviceType = board };

        // Core's board table is the bootstrap the capability document overlays, so going through
        // it must not change the answer for the hardware that exists today. This is the case the
        // bench run also covers, on a real Nq1 over USB.
        device.Metadata.Capabilities = DeviceCapabilities.FromDeviceType(board);

        Assert.True(device.HasWincWifiModule);
    }
}
