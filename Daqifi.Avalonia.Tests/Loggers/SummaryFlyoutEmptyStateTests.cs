using System.ComponentModel;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Logger;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Issue #249: with nothing reporting, the lower two thirds of the Log Summary flyout rendered as
/// bare panel. The pane now swaps between the per-device sections and a named empty state on
/// <see cref="SummaryLogger.HasDevices"/>.
///
/// <para>The swap itself is XAML and is verified by the parity-audit capture, not here — views in
/// this repo carry no <c>x:DataType</c>, so an <c>IsVisible</c> binding resolves by reflection at
/// run time and no test in this project can see it. What IS testable, and what these tests pin, is
/// the contract that binding depends on: that the property exists under the exact name the XAML
/// binds, that it agrees with the collection rendered beside it, and above all that it
/// change-notifies on every transition.</para>
///
/// <para>That last point is the one with teeth. <see cref="SummaryLogger.Devices"/> is an
/// <c>IEnumerable</c> over a list replaced wholesale on each publish rather than an observable
/// collection, so nothing else in the pane would notice the window emptying. Were
/// <c>HasDevices</c> to stop notifying, the flyout would keep whichever half it drew first — an
/// empty state stranded over live per-channel figures, or, after <c>Reset</c>, a table of numbers
/// from a window the user has just discarded. The second is the worse of the two and is the
/// reason <see cref="Reset_returns_to_the_empty_state_and_notifies"/> exists.</para>
/// </summary>
public class SummaryFlyoutEmptyStateTests
{
    private static DeviceMessage AMessageFrom(string deviceName = "Nq1-TEST") => new()
    {
        DeviceName = deviceName,
        DeviceSerialNo = "SN-0001",
        DeviceStatus = 0,
    };

    /// <summary>
    /// Recording, and republishing on every single device message so a test does not have to send
    /// a thousand of them to see the panel refresh.
    /// </summary>
    private static SummaryLogger ARecordingLogger() => new() { Enabled = true, SampleSize = 1 };

    /// <summary>Records every <c>HasDevices</c> change notification the logger raises.</summary>
    private static List<string> WatchHasDevices(SummaryLogger logger)
    {
        var raised = new List<string>();
        ((INotifyPropertyChanged)logger).PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(SummaryLogger.HasDevices)) { raised.Add(e.PropertyName!); }
        };
        return raised;
    }

    /// <summary>
    /// The state the flyout is in every time a user opens it with nothing connected — the state
    /// that rendered as bare panel, and the one the capture in this PR shows named.
    /// </summary>
    [Fact]
    public void A_fresh_logger_has_no_devices()
    {
        var logger = new SummaryLogger();

        Assert.Empty(logger.Devices);
        Assert.False(logger.HasDevices);
    }

    [Fact]
    public void The_first_reporting_device_flips_the_flag_and_notifies()
    {
        var logger = ARecordingLogger();
        var raised = WatchHasDevices(logger);

        logger.Log(AMessageFrom());

        Assert.True(logger.HasDevices);
        Assert.Single(logger.Devices);
        Assert.NotEmpty(raised);
    }

    /// <summary>
    /// Reset discards the window, so the sections it was showing go with it. This is the
    /// transition a view could not observe for itself: <c>Devices</c> is handed back a different,
    /// empty list rather than being mutated, and raises no collection change of its own.
    /// </summary>
    [Fact]
    public void Reset_returns_to_the_empty_state_and_notifies()
    {
        var logger = ARecordingLogger();
        logger.Log(AMessageFrom());
        Assert.True(logger.HasDevices);

        var raised = WatchHasDevices(logger);
        logger.ResetCommand.Execute(null);

        Assert.False(logger.HasDevices);
        Assert.Empty(logger.Devices);
        Assert.NotEmpty(raised);
    }

    /// <summary>
    /// Stopping is not resetting. The final publish in <c>Stop</c> is what the user reads the
    /// finished window off, so the sections have to stay — an empty state here would throw away
    /// the figures the moment the toggle was flicked.
    /// </summary>
    [Fact]
    public void Stopping_keeps_the_devices_the_window_finished_with()
    {
        var logger = ARecordingLogger();
        logger.Log(AMessageFrom());

        logger.ToggleEnabledCommand.Execute(null);   // Enabled was true, so this stops it

        Assert.False(logger.Enabled);
        Assert.True(logger.HasDevices);
        Assert.Single(logger.Devices);
    }

    /// <summary>
    /// The flag describes what has been PUBLISHED, not what has been accumulated, because that is
    /// what the sections beside it render. A device that has reported but not yet crossed the
    /// refresh threshold has no section to show, so the empty state is still the honest half —
    /// and, more to the point, the two can never contradict each other, since both read the same
    /// published snapshot.
    /// </summary>
    [Fact]
    public void A_device_that_has_not_yet_crossed_the_refresh_threshold_shows_no_section()
    {
        var logger = new SummaryLogger { Enabled = true, SampleSize = 2 };

        logger.Log(AMessageFrom());

        Assert.Empty(logger.Devices);
        Assert.False(logger.HasDevices);

        logger.Log(AMessageFrom());

        Assert.Single(logger.Devices);
        Assert.True(logger.HasDevices);
    }

    /// <summary>
    /// Two boards get two sections, not one merged row — the same per-device split the rest of
    /// this logger is built around, restated here because it is what makes the list worth swapping
    /// in at all.
    /// </summary>
    [Fact]
    public void Two_reporting_devices_produce_two_sections()
    {
        var logger = ARecordingLogger();

        logger.Log(AMessageFrom("Nq1-AAAA"));
        logger.Log(AMessageFrom("Nq1-BBBB"));

        Assert.True(logger.HasDevices);
        Assert.Collection(
            logger.Devices,
            first => Assert.Equal("Nq1-AAAA", first.Name),
            second => Assert.Equal("Nq1-BBBB", second.Name));
    }
}
