using Daqifi.Core.Firmware;
using Daqifi.Desktop.Device.Firmware;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device.Firmware;

/// <summary>
/// Pins the WiFi chip-info probe: how many times the app asks a just-woken WINC module what
/// firmware it carries, and what it concludes when the module never answers.
///
/// <para>
/// The behaviour matters because of what sits on the other side of the answer. A module that
/// reports nothing is treated as "needs a flash"
/// (<c>FirmwareUpdateCoordinator.WifiFirmwareNeedsFlash</c> maps a null result to
/// <c>Unknown</c> → flash), so a probe that gives up too early sends the user into a needless
/// multi-minute reflash of already-current firmware. Right after a PIC32 update the application
/// is up while the WiFi subsystem is still starting, and a WINC whose state machine has not
/// reached INITIALIZED answers SCPI <c>-200</c> instead of JSON — both clear within seconds,
/// which is exactly what the retry budget is for.
/// </para>
///
/// <para>
/// The app used to hand-roll this loop in two places (the firmware coordinator and the
/// connect-time probe in <c>DaqifiViewModel</c>), each a copy of the loop Daqifi.Core already
/// owns. These tests exercise the app's retry <em>policy</em> composed with Core's
/// <see cref="LanChipInfoProviderExtensions.GetLanChipInfoWithRetryAsync"/> mechanism, which is
/// the pairing the app actually ships — so they hold across that unification rather than
/// describing whichever copy happened to run.
/// </para>
///
/// <para>
/// No hardware: the probe's only seam is <see cref="ILanChipInfoProvider"/>, so a scripted fake
/// says what the module would have said.
/// </para>
/// </summary>
public class WifiChipInfoProbeTests
{
    /// <summary>Fails fast if a cancellation test ever stops observing its token.</summary>
    private static readonly TimeSpan UnwindTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The retry budget the app actually ships — read from the shipped constant rather than
    /// restated here, so these tests fail when the budget changes instead of describing a copy
    /// of it that nothing uses.
    /// </summary>
    private static LanChipInfoRetryOptions AppPolicy() =>
        FirmwareUpdateCoordinator.WifiChipInfoRetryOptions;

    /// <summary>
    /// The same policy with the pause removed. The pause's <em>value</em> is asserted directly in
    /// <see cref="The_probe_budget_is_three_attempts_two_seconds_apart_with_no_ceiling_and_no_lan_kick"/>;
    /// spending it for real here would only make these tests slow.
    /// </summary>
    private static LanChipInfoRetryOptions FastPolicy() => AppPolicy() with { RetryDelay = TimeSpan.Zero };

    /// <summary>
    /// The budget itself, spelled out. A probe that gives up after one attempt is what reflashes
    /// up-to-date modules, and an APPLY kick or a wall-clock ceiling would both put something on
    /// the wire — or take an attempt away — that this app has never done.
    /// </summary>
    [Fact]
    public void The_probe_budget_is_three_attempts_two_seconds_apart_with_no_ceiling_and_no_lan_kick()
    {
        var policy = AppPolicy();

        Assert.Equal(3, policy.MaxAttempts);
        Assert.Equal(TimeSpan.FromSeconds(2), policy.RetryDelay);
        Assert.Equal(Timeout.InfiniteTimeSpan, policy.TotalTimeout);
        Assert.False(policy.KickLanApplyOnNotInitialized);
    }

    /// <summary>
    /// A module that never answers is asked three times — not once — before the app concludes
    /// anything, and the exhausted probe reports "unavailable" rather than throwing.
    /// </summary>
    [Fact]
    public async Task A_silent_module_is_asked_three_times_and_then_reported_unavailable()
    {
        var device = ScriptedProvider.ThatAlwaysThrows();

        var result = await device.GetLanChipInfoWithRetryAsync(FastPolicy());

        Assert.Equal(3, device.Calls);
        Assert.False(result.Succeeded);
        Assert.Null(result.ChipInfo);
    }

    /// <summary>
    /// The point of the budget: a WINC that is merely slow to start reports the version it
    /// actually carries, instead of being reflashed because the first query lost a race.
    /// </summary>
    [Fact]
    public async Task A_module_that_answers_late_still_reports_its_version()
    {
        var device = ScriptedProvider.That(
            Outcome.Throws(new InvalidOperationException("WINC still starting")),
            Outcome.Throws(new LanNotInitializedException("SCPI -200")),
            Outcome.Returns(ChipInfo("19.7.7")));

        var result = await device.GetLanChipInfoWithRetryAsync(FastPolicy());

        Assert.Equal(3, device.Calls);
        Assert.True(result.Succeeded);
        Assert.Equal("19.7.7", result.ChipInfo!.FwVersion);
    }

    /// <summary>
    /// An unrecognized response comes back as a null <see cref="LanChipInfo"/> rather than an
    /// exception, and has to be retried just the same — it is the same transient startup window.
    /// </summary>
    [Fact]
    public async Task An_unrecognized_response_is_retried_like_a_failure()
    {
        var device = ScriptedProvider.That(
            Outcome.Returns(null),
            Outcome.Returns(null),
            Outcome.Returns(ChipInfo("19.5.4")));

        var result = await device.GetLanChipInfoWithRetryAsync(FastPolicy());

        Assert.Equal(3, device.Calls);
        Assert.Equal("19.5.4", result.ChipInfo!.FwVersion);
    }

    /// <summary>
    /// The hardware-safety guarantee. A firmware flash cancels the probe precisely so the probe's
    /// SCPI exchange can never overlap the flash and corrupt the bootloader handshake — so a probe
    /// handed an already-cancelled token must put nothing on the wire at all, not "one last query".
    /// </summary>
    [Fact]
    public async Task A_probe_cancelled_before_it_starts_never_queries_the_device()
    {
        var device = ScriptedProvider.ThatAlwaysThrows();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => device.GetLanChipInfoWithRetryAsync(FastPolicy(), cancellationToken: cts.Token));

        Assert.Equal(0, device.Calls);
    }

    /// <summary>
    /// Cancelling a probe that is already waiting out its retry pause surfaces as cancellation to
    /// the caller, rather than being absorbed into an "unavailable" result — the callers tell the
    /// two apart (a cancelled probe is re-run on reconnect; an unavailable one is a verdict).
    /// </summary>
    [Fact]
    public async Task Cancelling_a_running_probe_surfaces_as_cancellation()
    {
        var device = ScriptedProvider.ThatAlwaysThrows();
        using var cts = new CancellationTokenSource();

        // A pause long enough that the probe is certainly still inside it when Cancel arrives.
        var probe = device.GetLanChipInfoWithRetryAsync(
            AppPolicy() with { RetryDelay = TimeSpan.FromMinutes(1) },
            cancellationToken: cts.Token);

        await device.WaitForFirstCallAsync();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => probe.WaitAsync(UnwindTimeout));
    }

    /// <summary>
    /// The distinction a field log has to preserve. Both of these end the same way — nothing was
    /// read, so <c>WifiFirmwareNeedsFlash</c> maps the null to <c>Unknown</c> and the user is sent
    /// into a multi-minute reflash — but they mean opposite things to whoever reads the log
    /// afterwards: a WINC still booting (SCPI <c>-200</c>) says the retry budget was too small,
    /// while a module that never answered may be genuinely dead. Core tells the two apart in
    /// <c>WasLanNotInitialized</c>; before this the app discarded it and logged one sentence for
    /// both, and Core's own per-attempt lines are <c>LogDebug</c>, which never reach the file.
    /// </summary>
    [Fact]
    public async Task A_module_stuck_uninitialized_reads_differently_from_one_that_never_answered()
    {
        var stuckUninitialized = await ScriptedProvider
            .ThatAlwaysThrows(new LanNotInitializedException("SCPI -200"))
            .GetLanChipInfoWithRetryAsync(FastPolicy());
        var neverAnswered = await ScriptedProvider
            .ThatAlwaysThrows(new TimeoutException("serial read timed out"))
            .GetLanChipInfoWithRetryAsync(FastPolicy());

        // Same outcome — which is exactly why the outcome alone was not enough.
        Assert.Null(stuckUninitialized.ChipInfo);
        Assert.Null(neverAnswered.ChipInfo);

        var stuckReason = FirmwareUpdateCoordinator.DescribeUnreadableModule(stuckUninitialized);
        var silentReason = FirmwareUpdateCoordinator.DescribeUnreadableModule(neverAnswered);

        Assert.NotEqual(stuckReason, silentReason);
        Assert.Contains("uninitialized WINC state machine", stuckReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("-200", stuckReason, StringComparison.Ordinal);
        Assert.Contains("failed some other way", silentReason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Neither clause may claim more than the flag establishes. <c>WasLanNotInitialized</c>
    /// classifies the <em>terminal</em> attempt only, so a field log must not assert what earlier
    /// attempts did, nor name the specific error that ended the probe — Core resets the flag after
    /// any other failure, and that failure can be a timeout, an unparseable response, or any other
    /// provider exception. Inventing either would mislead support about the startup sequence.
    /// </summary>
    [Fact]
    public async Task Neither_clause_claims_a_probe_history_or_a_specific_terminal_error()
    {
        var stuck = FirmwareUpdateCoordinator.DescribeUnreadableModule(
            await ScriptedProvider.ThatAlwaysThrows(new LanNotInitializedException("SCPI -200"))
                .GetLanChipInfoWithRetryAsync(FastPolicy()));
        var other = FirmwareUpdateCoordinator.DescribeUnreadableModule(
            await ScriptedProvider.ThatAlwaysThrows(new InvalidOperationException("something else"))
                .GetLanChipInfoWithRetryAsync(FastPolicy()));

        // Scoped to the attempt the flag actually describes.
        Assert.Contains("final attempt", stuck, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("final attempt", other, StringComparison.OrdinalIgnoreCase);

        // No claim about what earlier attempts did...
        Assert.DoesNotContain("never", other, StringComparison.OrdinalIgnoreCase);
        // ...and no guess at which error ended it. This probe threw neither of these.
        Assert.DoesNotContain("timed out", other, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("unparseable", other, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The reason is a clause appended to an outcome line that is already written, so it has to
    /// compose into that sentence rather than stand alone — no new log line, and no log line that
    /// begins mid-thought.
    /// </summary>
    [Fact]
    public async Task The_reason_composes_onto_the_existing_outcome_line()
    {
        var probe = await ScriptedProvider
            .ThatAlwaysThrows(new LanNotInitializedException("SCPI -200"))
            .GetLanChipInfoWithRetryAsync(FastPolicy());

        var reason = FirmwareUpdateCoordinator.DescribeUnreadableModule(probe);

        Assert.StartsWith(" — ", reason, StringComparison.Ordinal);
        // A semicolon would collide with the coordinator's own "…; continuing with WiFi update."
        Assert.DoesNotContain(';', reason);
    }

    /// <summary>
    /// The regression this change could itself introduce: a module that answered is not
    /// unreadable, so the lines that report a real version — "is up to date (19.7.7)" and a
    /// genuine "needs a flash (reported: 19.5.4…)" — must not gain a failure clause. Core
    /// documents <c>WasLanNotInitialized</c> as meaningless when <c>ChipInfo</c> is non-null, so
    /// reading it there would print a reason for a probe that never failed.
    /// </summary>
    [Fact]
    public async Task A_module_that_answered_carries_no_failure_reason()
    {
        // An answer that arrives only after a not-initialized attempt — so the flag's underlying
        // condition genuinely occurred during this probe, and must still not be reported.
        var probe = await ScriptedProvider.That(
                Outcome.Throws(new LanNotInitializedException("SCPI -200")),
                Outcome.Returns(ChipInfo("19.5.4")))
            .GetLanChipInfoWithRetryAsync(FastPolicy());

        Assert.NotNull(probe.ChipInfo);
        Assert.Equal(string.Empty, FirmwareUpdateCoordinator.DescribeUnreadableModule(probe));
    }

    /// <summary>
    /// <c>WasLanNotInitialized</c> describes the <em>terminal</em> failure — Core resets it on any
    /// other kind — so a module that reported <c>-200</c> twice and then timed out must not read as
    /// the self-clearing case. Getting that backwards would tell support to wait out a module that
    /// is actually dead. Equally, it must not read as "never reported an uninitialized state",
    /// which this probe would make a lie: it reported one twice.
    /// </summary>
    [Fact]
    public async Task An_early_uninitialized_report_neither_survives_nor_is_denied()
    {
        var probe = await ScriptedProvider.That(
                Outcome.Throws(new LanNotInitializedException("SCPI -200")),
                Outcome.Throws(new LanNotInitializedException("SCPI -200")),
                Outcome.Throws(new TimeoutException("serial read timed out")))
            .GetLanChipInfoWithRetryAsync(FastPolicy());

        Assert.Null(probe.ChipInfo);
        Assert.False(probe.WasLanNotInitialized);

        var reason = FirmwareUpdateCoordinator.DescribeUnreadableModule(probe);

        // Not the self-clearing verdict...
        Assert.DoesNotContain("normally clears on its own", reason, StringComparison.OrdinalIgnoreCase);
        // ...and not a denial that -200 was ever seen, because it was — twice.
        Assert.DoesNotContain("never", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("final attempt failed some other way", reason, StringComparison.OrdinalIgnoreCase);
    }

    #region Helpers
    private static LanChipInfo ChipInfo(string version) =>
        new() { ChipId = 0x1503A0, FwVersion = version, BuildDate = "2020-01-01" };

    /// <summary>One scripted answer from the WiFi module.</summary>
    private sealed record Outcome(LanChipInfo? Value, Exception? Error)
    {
        public static Outcome Returns(LanChipInfo? value) => new(value, null);
        public static Outcome Throws(Exception error) => new(null, error);
    }

    /// <summary>
    /// A WiFi module whose answers are scripted per attempt, counting how many times it was
    /// actually asked. Deliberately not an <c>IStreamingDevice</c>: the probe can only send a
    /// <c>LAN:APPLY</c> kick through one, so this fake also proves nothing was kicked.
    /// </summary>
    private sealed class ScriptedProvider : ILanChipInfoProvider
    {
        private readonly Outcome[] _script;
        private readonly Outcome _fallback;
        private readonly TaskCompletionSource _firstCall =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private ScriptedProvider(Outcome[] script, Outcome fallback)
        {
            _script = script;
            _fallback = fallback;
        }

        public int Calls { get; private set; }

        public static ScriptedProvider That(params Outcome[] script) =>
            new(script, Outcome.Throws(new InvalidOperationException("script exhausted")));

        public static ScriptedProvider ThatAlwaysThrows() =>
            ThatAlwaysThrows(new InvalidOperationException("WINC not answering"));

        /// <summary>A module that fails the same way on every attempt, for a named reason.</summary>
        public static ScriptedProvider ThatAlwaysThrows(Exception error) =>
            new([], Outcome.Throws(error));

        public Task WaitForFirstCallAsync() => _firstCall.Task.WaitAsync(UnwindTimeout);

        public Task<LanChipInfo?> GetLanChipInfoAsync(CancellationToken cancellationToken = default)
        {
            var outcome = Calls < _script.Length ? _script[Calls] : _fallback;
            Calls++;
            _firstCall.TrySetResult();

            return outcome.Error is not null
                ? Task.FromException<LanChipInfo?>(outcome.Error)
                : Task.FromResult(outcome.Value);
        }
    }
    #endregion
}
