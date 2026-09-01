using Daqifi.Core.Device;
using Daqifi.Desktop;
using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// Pins the two decisions the background-failure log lines rest on: what of a failed command is
/// safe to write down, and which failures are the app's fault.
///
/// Both are policy the app owns rather than anything Core decides, and both are invisible in
/// normal operation — a redaction that stops working produces a log file that looks fine and
/// contains a password, and a severity that drifts produces either a Sentry flood or silence.
/// </summary>
public class ConnectionManagerDiagnosticsTests
{
    /// <summary>
    /// The reason <c>DescribeCommand</c> exists at all. <c>SYSTem:COMMunicate:LAN:PASs</c> carries
    /// the user's WiFi password as its argument, and a send failure is exactly when the app writes
    /// the command that failed to the log file users attach to support reports.
    /// </summary>
    [Fact]
    public void A_failed_wifi_password_command_is_logged_without_the_password()
    {
        var described = ConnectionManager.DescribeCommand("SYSTem:COMMunicate:LAN:PASs \"hunter2\"");

        Assert.Equal("SYSTem:COMMunicate:LAN:PASs", described);
        Assert.DoesNotContain("hunter2", described);
    }

    /// <summary>
    /// Verb-only, not password-only: keeping every argument except the password would leave the
    /// next secret-bearing command to be noticed by hand. The SSID goes too.
    /// </summary>
    [Theory]
    [InlineData("SYSTem:COMMunicate:LAN:SSID \"MyHomeNetwork\"", "SYSTem:COMMunicate:LAN:SSID")]
    [InlineData("SOURce:VOLTage:LEVel 3.3", "SOURce:VOLTage:LEVel")]
    [InlineData("SYSTem:STARt", "SYSTem:STARt")]
    [InlineData("  SYSTem:STOP  ", "SYSTem:STOP")]
    public void Only_the_verb_survives(string command, string expected)
    {
        Assert.Equal(expected, ConnectionManager.DescribeCommand(command));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void A_command_with_no_content_says_so_rather_than_logging_nothing(string? command)
    {
        Assert.Equal("(empty)", ConnectionManager.DescribeCommand(command));
    }

    /// <summary>
    /// A malformed or oversized payload with no space in it must not turn one failure into a wall
    /// of log — the cap is what keeps a corrupt buffer from being written out verbatim.
    /// </summary>
    [Fact]
    public void An_unreasonably_long_verb_is_truncated()
    {
        var described = ConnectionManager.DescribeCommand(new string('X', 500));

        Assert.Equal(new string('X', 64) + "...", described);
    }

    /// <summary>
    /// Every source that describes the state of the link is a Warning. Routing these to Error
    /// captures them to Sentry, where the volume tracks how often users unplug things and the
    /// noise buries real bugs — a flood this codebase's upstream has taken three times.
    /// </summary>
    [Theory]
    [InlineData(DeviceErrorSource.MessageConsumer)]
    [InlineData(DeviceErrorSource.StreamDecode)]
    [InlineData(DeviceErrorSource.Reconnect)]
    public void A_failure_of_the_link_is_not_an_app_bug(DeviceErrorSource source)
    {
        Assert.False(ConnectionManager.IsAppBug(source));
    }

    /// <summary>
    /// <see cref="DeviceErrorSource.StatusNotification"/> means a <c>StatusChanged</c> subscriber
    /// threw while being notified of a connection transition. Every subscriber to that event is
    /// this app's own code, and the usual way one throws is a cross-thread mutation of bound state
    /// from Core's transport thread — a real defect, raised on the drop path, which is where it is
    /// hardest to notice. <see cref="DeviceErrorSource.Unknown"/> means Core hit a failure it could
    /// not classify, which no Core path produces today.
    /// </summary>
    [Theory]
    [InlineData(DeviceErrorSource.Unknown)]
    [InlineData(DeviceErrorSource.StatusNotification)]
    public void A_failure_of_this_app_is_an_app_bug(DeviceErrorSource source)
    {
        Assert.True(ConnectionManager.IsAppBug(source));
    }

    /// <summary>
    /// The severity table has a default arm, so a member Core adds later would be classified as
    /// "not an app bug" silently and forever. This is the tripwire: a Core bump that adds a source
    /// fails here, and someone decides where it belongs instead of inheriting the default.
    /// </summary>
    [Fact]
    public void Every_source_Core_defines_has_been_classified_deliberately()
    {
        var classified = new[]
        {
            DeviceErrorSource.Unknown,
            DeviceErrorSource.MessageConsumer,
            DeviceErrorSource.StreamDecode,
            DeviceErrorSource.Reconnect,
            DeviceErrorSource.StatusNotification,
        };

        Assert.Equal(classified.Order(), Enum.GetValues<DeviceErrorSource>().Order());
    }
}
