using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.SdCard;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Tests for <see cref="SdCardFailureClassifier"/>, which turns an exception from an SD card
/// operation into the sentence the user reads and the decision of whether the rest of a batch
/// import is worth attempting.
///
/// Two things are being pinned. The first is that the advice fits the failure: "power-cycle the
/// device" is expensive advice — it costs a support conversation and an interrupted experiment —
/// and must not be given for a condition that says nothing about the hardware. The second is
/// <see cref="SdCardFailure.IsCardUnavailable"/>, which aborts a batch: because the device lists
/// files in the same order every time, a wrongly card-wide verdict makes every file after the
/// failing one permanently unreachable through Import All.
/// </summary>
public class SdCardFailureClassifierTests
{
    private static SdCardTransferStalledException Stalled(SdCardTransferStallReason reason) =>
        new("log.bin", bytesReceived: 0, reason, TimeSpan.FromSeconds(90));

    #region An empty transfer is not a device fault

    [Fact]
    public void An_empty_transfer_does_not_tell_the_user_to_power_cycle()
    {
        // Core only raises this now for a marker-only transfer of a file its listing called
        // non-empty, or whose size it could not read — still ambiguous enough that a flat
        // "your SD subsystem is not responding" is a claim the app cannot support.
        var failure = SdCardFailureClassifier.Classify(new SdCardEmptyTransferException("log.bin"));

        Assert.Equal(SdCardFailureClassifier.EMPTY_TRANSFER_GUIDANCE, failure.Guidance);
        Assert.NotEqual(SdCardFailureClassifier.POWER_CYCLE_GUIDANCE, failure.Guidance);
        Assert.True(failure.IsExpectedDeviceCondition);
        Assert.False(failure.IsCardUnavailable);
    }

    [Fact]
    public void An_empty_transfer_does_not_abandon_the_rest_of_the_batch()
    {
        var failure = SdCardFailureClassifier.Classify(new SdCardEmptyTransferException("log.bin", 0));

        Assert.False(failure.IsCardUnavailable);
    }

    #endregion

    #region A stall is described by its reason

    [Fact]
    public void A_closed_transport_is_reported_as_a_lost_connection_not_a_wedged_card()
    {
        var failure = SdCardFailureClassifier.Classify(Stalled(SdCardTransferStallReason.TransportClosed));

        Assert.Equal(SdCardFailureClassifier.TRANSPORT_CLOSED_GUIDANCE, failure.Guidance);
        Assert.Contains("connection to the device dropped", failure.StatusMessage, StringComparison.Ordinal);
        // Core states a retry on a closed transport cannot succeed, so there is nothing to gain
        // from trying the remaining files.
        Assert.True(failure.IsCardUnavailable);
    }

    [Fact]
    public void A_single_quiet_read_is_reported_as_one_incomplete_file()
    {
        // Core's per-read stall fires in well under a second and happens on healthy hardware —
        // an SD read-latency spike, USB backpressure, a GC pause. Aborting the batch on it would
        // skip every later file over a hiccup.
        var failure = SdCardFailureClassifier.Classify(Stalled(SdCardTransferStallReason.NoDataReceived));

        Assert.Equal(SdCardFailureClassifier.INCOMPLETE_TRANSFER_GUIDANCE, failure.Guidance);
        Assert.False(failure.IsCardUnavailable);
    }

    [Fact]
    public void An_elapsed_transfer_deadline_is_the_one_stall_that_blames_the_device()
    {
        var failure = SdCardFailureClassifier.Classify(Stalled(SdCardTransferStallReason.TransferTimeout));

        Assert.Equal(SdCardFailureClassifier.POWER_CYCLE_GUIDANCE, failure.Guidance);
        Assert.True(failure.IsCardUnavailable);
    }

    [Fact]
    public void The_three_stall_reasons_do_not_all_read_the_same()
    {
        // The whole point of typing the stall: before this, every stall produced one sentence.
        var guidance = new[]
        {
            SdCardTransferStallReason.TransportClosed,
            SdCardTransferStallReason.NoDataReceived,
            SdCardTransferStallReason.TransferTimeout,
        }.Select(r => SdCardFailureClassifier.Classify(Stalled(r)).Guidance).ToList();

        Assert.Equal(3, guidance.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void A_stall_is_an_expected_device_condition_rather_than_an_app_defect()
    {
        // IsExpectedDeviceCondition is what keeps a stall off the Error/Sentry path.
        Assert.All(
            new[]
            {
                SdCardTransferStallReason.TransportClosed,
                SdCardTransferStallReason.NoDataReceived,
                SdCardTransferStallReason.TransferTimeout,
            },
            reason => Assert.True(SdCardFailureClassifier.Classify(Stalled(reason)).IsExpectedDeviceCondition));
    }

    #endregion

    #region The arms this port carries that upstream does not

    [Fact]
    public void An_unfinished_file_listing_is_still_a_transport_problem()
    {
        var failure = SdCardFailureClassifier.Classify(new SdCardListIncompleteException([]));

        Assert.Equal(SdCardFailureClassifier.TRANSPORT_GONE_GUIDANCE, failure.Guidance);
        Assert.True(failure.IsExpectedDeviceCondition);
        Assert.True(failure.IsCardUnavailable);
    }

    [Fact]
    public void Firmware_too_old_for_sd_over_wifi_is_still_a_firmware_message()
    {
        var failure = SdCardFailureClassifier.Classify(
            new FeatureNotSupportedException(DeviceFeature.SdFileTransferOverWifi));

        Assert.Equal(SdCardFailureClassifier.FIRMWARE_TOO_OLD_FOR_WIFI_SD_GUIDANCE, failure.Guidance);
        Assert.True(failure.IsExpectedDeviceCondition);
        Assert.True(failure.IsCardUnavailable);
    }

    [Fact]
    public void A_transport_that_is_gone_is_still_a_reconnect_message()
    {
        var failure = SdCardFailureClassifier.Classify(new TransportNotConnectedException());

        Assert.Equal(SdCardFailureClassifier.TRANSPORT_GONE_GUIDANCE, failure.Guidance);
        Assert.True(failure.IsExpectedDeviceCondition);
        Assert.True(failure.IsCardUnavailable);
    }

    [Fact]
    public void A_device_already_known_to_be_disconnected_is_still_a_reconnect_message()
    {
        var failure = SdCardFailureClassifier.Classify(new DeviceNotConnectedException());

        Assert.Equal(SdCardFailureClassifier.TRANSPORT_GONE_GUIDANCE, failure.Guidance);
        Assert.True(failure.IsExpectedDeviceCondition);
        Assert.True(failure.IsCardUnavailable);
    }

    [Theory]
    [InlineData(SdOperationBlockedReason.SdCardLogging)]
    [InlineData(SdOperationBlockedReason.Streaming)]
    public void A_busy_device_is_still_shown_as_busy_rather_than_an_error(SdOperationBlockedReason reason)
    {
        var failure = SdCardFailureClassifier.Classify(new SdOperationBlockedException(reason));

        Assert.Equal(SdCardState.Busy, failure.State);
        Assert.True(failure.IsExpectedDeviceCondition);
        Assert.True(failure.IsCardUnavailable);
        Assert.Equal(
            reason == SdOperationBlockedReason.SdCardLogging
                ? SdCardFailureClassifier.STOP_SD_LOGGING_GUIDANCE
                : SdCardFailureClassifier.STOP_STREAMING_GUIDANCE,
            failure.Guidance);
    }

    #endregion

    #region Everything else keeps the defect path

    [Fact]
    public void A_bare_timeout_is_not_treated_as_an_sd_card_condition()
    {
        // Only the importer normalises the timeout that IS about the card, at the download call
        // site where the scope makes it safe. One arriving from anywhere else must keep the
        // Error/Sentry path rather than be reported as a device fault.
        var failure = SdCardFailureClassifier.Classify(new TimeoutException("something else timed out"));

        Assert.False(failure.IsExpectedDeviceCondition);
        Assert.Equal(SdCardFailureClassifier.UNEXPECTED_FAILURE_GUIDANCE, failure.Guidance);
    }

    [Fact]
    public void A_download_that_produced_no_local_file_is_an_app_defect_not_a_device_condition()
    {
        // The importer raises a plain InvalidOperationException for a broken IStreamingDevice
        // that reports success without writing a file. That belongs on the Error path, not in
        // front of the user as SD card advice.
        var failure = SdCardFailureClassifier.Classify(
            new InvalidOperationException("reported success without producing a local file"));

        Assert.False(failure.IsExpectedDeviceCondition);
        Assert.Equal(SdCardFailureClassifier.UNEXPECTED_FAILURE_GUIDANCE, failure.Guidance);
    }

    [Fact]
    public void Classify_rejects_a_null_exception()
    {
        Assert.Throws<ArgumentNullException>(() => SdCardFailureClassifier.Classify(null!));
    }

    #endregion
}
