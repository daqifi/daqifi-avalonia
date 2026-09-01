using Daqifi.Core.Firmware;
using Daqifi.Desktop.Device.Firmware;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Pins <see cref="FirmwareFailureClassifier"/>, the one decision that separates "your firmware is on
/// the board, power-cycle it" from "the flash failed, do it again".
///
/// Getting it wrong in either direction is user-visible and expensive. Too narrow (the shipped
/// behaviour before this class existed) and a WiFi flash that wrote successfully and then timed out
/// re-enumerating its serial port is reported as a failure, telling the user to re-flash firmware the
/// board already has — and files a Sentry error for a successful operation. Too wide and a genuine
/// bad flash is announced as a success, so the user power-cycles a device whose firmware is not
/// actually installed and never sees the real error.
///
/// The classifier is keyed on <see cref="FirmwareUpdateException.FailedState"/> alone and never on
/// <see cref="FirmwareUpdateException.Operation"/>, which carries free-form prose Core is free to
/// reword. Several cases below hold that line explicitly.
/// </summary>
public class FirmwareFailureClassifierTests
{
    /// <summary>The two states that mean "written and verified, only the reconnect timed out".</summary>
    private static readonly FirmwareUpdateState[] PostFlashReconnectStates =
    [
        // PIC32: the last step, entered only after erase + program + CRC-verify all passed.
        FirmwareUpdateState.JumpingToApp,
        // WiFi module: entered only after the WINC flash tool printed its success marker.
        FirmwareUpdateState.ReconnectingAfterFlash,
    ];

    private static FirmwareUpdateException Failure(
        FirmwareUpdateState failedState,
        string operation = "test operation") =>
        new(failedState, operation, $"Firmware update failed in {failedState}.");

    [Fact]
    public void Wifi_post_flash_reconnect_timeout_is_not_a_flash_failure()
    {
        // The regression this class exists for. Before it, only JumpingToApp was carved out, so this
        // exception fell through to the generic branch: an Error log (hence a Sentry event) and a
        // dialog telling the user to power-cycle and try again.
        Assert.True(FirmwareFailureClassifier.IsPostFlashReconnectTimeout(
            Failure(FirmwareUpdateState.ReconnectingAfterFlash)));
    }

    [Fact]
    public void Pic32_jump_to_app_timeout_is_still_not_a_flash_failure()
    {
        // The carve-out that already shipped. It must survive the widening.
        Assert.True(FirmwareFailureClassifier.IsPostFlashReconnectTimeout(
            Failure(FirmwareUpdateState.JumpingToApp)));
    }

    [Fact]
    public void Crc_verify_failure_is_still_reported_as_a_failure()
    {
        // Verifying is the PIC32's flash-CRC read-back. Core's own docs call it "a genuine flash
        // failure", explicitly opposite in severity to ReconnectingAfterFlash. Downgrading it would
        // tell a user with a half-written image that their firmware installed.
        Assert.False(FirmwareFailureClassifier.IsPostFlashReconnectTimeout(
            Failure(FirmwareUpdateState.Verifying)));
    }

    [Fact]
    public void Every_other_state_keeps_the_failure_path()
    {
        // Exhaustive over the enum rather than a hand-picked list, so a state Core adds later defaults
        // to "real failure" and this test fails if someone widens the carve-out without saying why.
        foreach (var state in Enum.GetValues<FirmwareUpdateState>())
        {
            if (PostFlashReconnectStates.Contains(state))
            {
                continue;
            }

            Assert.False(
                FirmwareFailureClassifier.IsPostFlashReconnectTimeout(Failure(state)),
                $"{state} must keep the Error/Sentry failure path.");
        }
    }

    [Fact]
    public void Classification_ignores_the_operation_text()
    {
        // Operation is whatever string Core passed to its last TransitionToState call — free-form
        // prose with no published constant. These two cases are what a text-matching classifier would
        // get backwards, and they are why the discriminator is structural.
        Assert.True(FirmwareFailureClassifier.IsPostFlashReconnectTimeout(
            Failure(FirmwareUpdateState.ReconnectingAfterFlash, operation: "Verifying flash CRC")));

        Assert.False(FirmwareFailureClassifier.IsPostFlashReconnectTimeout(
            Failure(FirmwareUpdateState.Verifying, operation: "Reconnecting after flash")));
    }

    [Fact]
    public void A_null_exception_is_rejected_rather_than_silently_classified()
    {
        // Not defensive noise: returning false for null would quietly send a caller down the failure
        // path, and returning true would announce an install that never happened.
        Assert.Throws<ArgumentNullException>(
            () => FirmwareFailureClassifier.IsPostFlashReconnectTimeout(null!));
    }

    [Fact]
    public void Wifi_message_says_the_wifi_firmware_installed()
    {
        var message = FirmwareFailureClassifier.BuildInstalledButNotReconnectedMessage(
            FirmwareUpdateState.ReconnectingAfterFlash);

        Assert.StartsWith("WiFi firmware was installed successfully", message, StringComparison.Ordinal);
        Assert.Contains("power-cycle", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Pic32_message_does_not_claim_the_wifi_module_was_flashed()
    {
        var message = FirmwareFailureClassifier.BuildInstalledButNotReconnectedMessage(
            FirmwareUpdateState.JumpingToApp);

        Assert.StartsWith("Firmware was installed successfully", message, StringComparison.Ordinal);
        Assert.DoesNotContain("WiFi", message, StringComparison.Ordinal);
        Assert.Contains("power-cycle", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Neither_message_tells_the_user_to_flash_again()
    {
        // The whole point of the change: the old WiFi dialog said "WiFi firmware flash failed.
        // Disconnect the device, ensure power is cycled, and try again." Re-flashing is exactly the
        // wrong instruction when the image is already on the board.
        foreach (var state in PostFlashReconnectStates)
        {
            var message = FirmwareFailureClassifier.BuildInstalledButNotReconnectedMessage(state);

            Assert.DoesNotContain("failed", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("try again", message, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_two_messages_are_not_the_same_text()
    {
        // A caller that dropped the failedState argument would still compile and still read as an
        // "installed" message; only this catches it.
        Assert.NotEqual(
            FirmwareFailureClassifier.BuildInstalledButNotReconnectedMessage(FirmwareUpdateState.JumpingToApp),
            FirmwareFailureClassifier.BuildInstalledButNotReconnectedMessage(FirmwareUpdateState.ReconnectingAfterFlash));
    }

    [Fact]
    public void Any_state_the_classifier_downgrades_gets_an_installed_message()
    {
        // The classifier and the message builder are called as a pair, so their agreement is part of
        // the contract: nothing the classifier lets through may be worded as a failure.
        foreach (var state in Enum.GetValues<FirmwareUpdateState>())
        {
            if (!FirmwareFailureClassifier.IsPostFlashReconnectTimeout(Failure(state)))
            {
                continue;
            }

            var message = FirmwareFailureClassifier.BuildInstalledButNotReconnectedMessage(state);
            Assert.Contains("installed successfully", message, StringComparison.Ordinal);
        }
    }
}
