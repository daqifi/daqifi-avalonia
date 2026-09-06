using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Communication.Transport;
using Daqifi.Core.Device;
using Daqifi.Core.Device.Capabilities;
using Daqifi.Desktop.Device;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Issue #272: the rate a session starts at has to fit under what the device will accept for the
/// channels enabled at that moment, not merely under the board's absolute ceiling.
///
/// <para>The two are far apart, and the gap is measured, not assumed. On the bench Nq1 (fw 3.7.2)
/// the capability document states <c>sample_rate_range_hz.max = 22000</c> — the sampling ISR's
/// envelope, reachable with almost nothing enabled — while <c>current_max_rate_hz</c> with all
/// sixteen analog inputs on is <b>3518</b>. Since firmware v3.5.0 a start above the latter is
/// answered with SCPI <c>-222</c> and nothing streams: four seconds at 7036 Hz on that board
/// produced <b>zero</b> analog frames, against 11160 for the same run at 3518 Hz. The user sees a
/// session that records nothing — the failure #248/#255 existed to remove.</para>
///
/// <para>The freshness half is what makes this more than a one-liner, and it is also measured. The
/// app reads the capability document once, inside <c>Connect()</c>, and at that moment no channel
/// is enabled: the bench board answers <c>current_max_rate_hz: 0</c> there. Zero is a real answer
/// ("nothing to stream"), which <c>SampleRateCap.Enforce</c> deliberately reads as "leave the rate
/// alone" — so the connect-time figure is not merely stale, it enforces nothing at all. These
/// cases therefore pin the re-read as part of the behaviour, not as an implementation detail.</para>
///
/// <para>Where the numbers come from: every figure above was read off the real board on
/// 2026-09-06 through pinned Core 1.7.0, with the enabled channel set and the streaming rate
/// snapshotted and restored.</para>
/// </summary>
public class CurrentRateCapEnforcementTests
{
    /// <summary>The bench Nq1's absolute ceiling — what the slider offers since #270/#273.</summary>
    private const int BoardCeilingHz = 22000;

    /// <summary>Its cap with all sixteen analog inputs enabled.</summary>
    private const int MeasuredCurrentCapHz = 3518;

    /// <summary>A rate the slider offers today and the board refuses outright.</summary>
    private const int OverCapRateHz = 7036;

    #region A rate over the cap does not reach the device
    /// <summary>
    /// The defect itself. Each row is a rate inside <c>1..22000</c> — so the slider offers it, the
    /// wrapper stores it and Core's own setter takes it — that the device would refuse for the
    /// channel set it has enabled.
    /// </summary>
    [Theory]
    [InlineData(OverCapRateHz, MeasuredCurrentCapHz)]
    [InlineData(BoardCeilingHz, MeasuredCurrentCapHz)]
    [InlineData(1000, 500)]
    [InlineData(2, 1)]
    public void A_rate_the_enabled_channels_cannot_sustain_is_lowered_to_the_cap(int rateHz, int capHz)
    {
        var core = CoreDeviceRefreshingTo(capHz);
        var device = WrapperAt(rateHz, core);

        device.HoldRateForHandoff(core);

        Assert.Equal(capHz, device.StreamingFrequency);
    }

    /// <summary>
    /// And the wrapper does not just report the lower rate — the device is left holding it. The
    /// wrapper's value is what the UI shows; Core's is what goes on the wire when streaming
    /// starts, and a session that disagreed with its own readout would be the original complaint
    /// wearing different clothes.
    /// </summary>
    [Fact]
    public void The_rate_the_wrapper_reports_is_the_rate_Core_is_left_holding()
    {
        var core = CoreDeviceRefreshingTo(MeasuredCurrentCapHz);
        var device = WrapperAt(OverCapRateHz, core);

        device.HoldRateForHandoff(core);

        Assert.Equal(MeasuredCurrentCapHz, core.StreamingFrequency);
        Assert.Equal(core.StreamingFrequency, device.StreamingFrequency);
    }

    /// <summary>
    /// A rate that already fits is not touched. The cap lowers a rate the hardware cannot run;
    /// it is not a second opinion on a rate the user chose and the device will honour.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    [InlineData(MeasuredCurrentCapHz)]
    public void A_rate_that_already_fits_under_the_cap_is_left_exactly_where_it_was(int rateHz)
    {
        var core = CoreDeviceRefreshingTo(MeasuredCurrentCapHz);
        var device = WrapperAt(rateHz, core);

        device.HoldRateForHandoff(core);

        Assert.Equal(rateHz, device.StreamingFrequency);
        Assert.Equal(rateHz, core.StreamingFrequency);
    }
    #endregion

    #region The cap is the one read at the handoff, not the one read at connect
    /// <summary>
    /// The whole reason this is not a one-liner. The document Core read during <c>Connect()</c>
    /// describes the channel set enabled then — none — so it carries the bench board's measured
    /// <c>current_max_rate_hz: 0</c>. Enforcing that would do nothing whatsoever. What is enforced
    /// is the answer to a read taken here, with the user's channels on.
    /// </summary>
    [Fact]
    public void The_cap_enforced_is_the_one_read_at_the_handoff_not_the_one_taken_at_connect()
    {
        var core = CoreDeviceRefreshingTo(MeasuredCurrentCapHz);
        core.Metadata.ApplyCapabilityDocument(DocumentCapping(0));
        var device = WrapperAt(OverCapRateHz, core);

        device.HoldRateForHandoff(core);

        Assert.Equal(1, core.RefreshCount);
        Assert.Equal(MeasuredCurrentCapHz, device.StreamingFrequency);
    }

    /// <summary>
    /// And a fresh answer of <c>0</c> — which is what the board says when nothing is enabled — is
    /// not a limit of zero hertz. Driving the rate to 0 would be both meaningless and impossible
    /// (Core rejects it), so the rate stands until the channel set changes again. This is Core's
    /// documented rule; it is pinned here because the app now depends on it.
    /// </summary>
    [Fact]
    public void A_fresh_cap_of_zero_means_nothing_is_enabled_and_leaves_the_rate_alone()
    {
        var core = CoreDeviceRefreshingTo(0);
        var device = WrapperAt(OverCapRateHz, core);

        device.HoldRateForHandoff(core);

        Assert.Equal(1, core.RefreshCount);
        Assert.Equal(OverCapRateHz, device.StreamingFrequency);
    }
    #endregion

    #region Devices that say nothing about their configuration keep working
    /// <summary>
    /// Every firmware below v3.5.0 publishes no capability document, so it has said nothing about
    /// how its channel set affects the rate. Core reports the board ceiling for those, which makes
    /// this a no-op: nothing that starts today starts less often afterwards.
    /// </summary>
    /// <remarks>
    /// Uses Core's real <c>ReadCapabilityDocumentAsync</c> — not the double's — against a
    /// connected device that has never reported a firmware version, which is the state a
    /// pre-3.5.0 board leaves it in. The read is skipped at Core's own feature gate.
    /// </remarks>
    [Fact]
    public void A_device_that_publishes_no_capability_document_is_not_newly_capped()
    {
        using var harness = ConnectedCoreDevice();
        var device = WrapperAt(OverCapRateHz, harness.CoreDevice);

        device.HoldRateForHandoff(harness.CoreDevice);

        Assert.Null(harness.CoreDevice.Metadata.CapabilityDocument);
        Assert.Equal(OverCapRateHz, device.StreamingFrequency);
    }

    /// <summary>
    /// A refresh that throws must not become a failure to record. The previous document stays in
    /// place — exactly where it stood before this step existed — so the last cap the device stated
    /// is still applied, and the start goes ahead.
    /// </summary>
    [Fact]
    public void A_refresh_that_fails_does_not_fail_the_start_and_the_last_known_cap_still_applies()
    {
        var core = new CapabilityRefreshingCoreDevice("core")
        {
            RefreshFailure = new DeviceNotConnectedException()
        };
        core.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = BoardCeilingHz };
        core.Metadata.ApplyCapabilityDocument(DocumentCapping(MeasuredCurrentCapHz));
        var device = WrapperAt(OverCapRateHz, core);

        device.HoldRateForHandoff(core);

        Assert.Equal(MeasuredCurrentCapHz, device.StreamingFrequency);
    }

    /// <summary>
    /// The same for a refresh that answers with nothing rather than throwing, which is the far more
    /// likely of the two: Core returns <c>null</c> — it does not throw — for an unanswered query,
    /// an unparseable reply, or a schema version it does not know.
    /// </summary>
    [Fact]
    public void A_refresh_that_answers_with_nothing_leaves_the_last_stated_cap_in_force()
    {
        var core = new CapabilityRefreshingCoreDevice("core") { RefreshedDocument = null };
        core.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = BoardCeilingHz };
        core.Metadata.ApplyCapabilityDocument(DocumentCapping(MeasuredCurrentCapHz));
        var device = WrapperAt(OverCapRateHz, core);

        device.HoldRateForHandoff(core);

        Assert.Equal(1, core.RefreshCount);
        Assert.Equal(MeasuredCurrentCapHz, device.StreamingFrequency);
    }

    /// <summary>
    /// The one case the cap genuinely cannot cover, pinned so it is a decision rather than an
    /// oversight: the refresh fails and the only cached figure is the inert connect-time
    /// <c>0</c>, so the rate goes to the device unvalidated and the device may refuse it.
    /// </summary>
    /// <remarks>
    /// The alternative — refusing to record when the capability read fails — was rejected. The
    /// document is enrichment, not a requirement (Core: "a device that cannot supply one is fully
    /// usable"), and the overwhelming majority of sessions run far below any cap, so refusing would
    /// newly block runs that work today over a read that failed. What the app does instead is say
    /// so: the rate it is about to use is logged alongside the fact that the cap is unknown, so a
    /// refused start has an explanation waiting rather than none. The rate itself is untouched —
    /// silently lowering it to a figure describing a different channel set would be worse than
    /// leaving it alone.
    /// </remarks>
    [Fact]
    public void A_failed_refresh_over_an_inert_cached_cap_still_starts_and_does_not_move_the_rate()
    {
        var core = new CapabilityRefreshingCoreDevice("core")
        {
            RefreshFailure = new DeviceNotConnectedException()
        };
        core.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = BoardCeilingHz };
        core.Metadata.ApplyCapabilityDocument(DocumentCapping(0));
        var device = WrapperAt(OverCapRateHz, core);

        device.HoldRateForHandoff(core);

        Assert.Equal(OverCapRateHz, device.StreamingFrequency);
        Assert.Equal(OverCapRateHz, core.StreamingFrequency);
    }
    #endregion

    #region The start path itself
    /// <summary>
    /// The end-to-end fact, and the only one of these that can run against unmodified
    /// <c>main</c>: what <c>InitializeStreaming</c> puts on the wire is the capped rate.
    /// </summary>
    /// <remarks>
    /// Core's <c>StartStreaming</c> emits <c>SYSTem:StartStreamData &lt;hz&gt;</c>, so the rate the
    /// device is actually asked for is readable off the transport rather than inferred from a
    /// property. Nothing here is stubbed but the device's answers: the wrapper, its start path,
    /// Core's session controller and Core's SCPI producer are all the production ones.
    /// <para>
    /// Its sibling <c>StartSdCardLogging</c> takes the same step through the same method, one line
    /// after the same <c>ClampRateToAdvertisedCeiling</c>, but cannot be driven this far here:
    /// Core's <c>StartSdCardLoggingAsync</c> waits on replies a capturing transport never sends.
    /// </para>
    /// </remarks>
    [Fact]
    public void InitializeStreaming_asks_the_device_for_the_capped_rate_not_the_one_it_was_given()
    {
        using var harness = ConnectedCoreDevice(cappingTo: MeasuredCurrentCapHz);
        var device = WrapperAt(OverCapRateHz, harness.CoreDevice);
        harness.Transport.ClearSent();

        device.InitializeStreaming();

        Assert.True(
            harness.Transport.WaitForSentText($"SYSTem:StartStreamData {MeasuredCurrentCapHz}", SendTimeout),
            $"Expected the start command to carry {MeasuredCurrentCapHz} Hz; saw: {harness.Transport.SentText}");
        Assert.DoesNotContain(
            $"SYSTem:StartStreamData {OverCapRateHz}", harness.Transport.SentText, StringComparison.Ordinal);
    }
    #endregion

    #region Helpers
    /// <summary>Core's producer flushes on its own thread, so the write lags the call.</summary>
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// The bench board's document reduced to the two fields these cases turn on. Built rather than
    /// parsed, so each test states what it depends on; parsing is Core's to cover.
    /// </summary>
    private static CapabilityDocument DocumentCapping(int currentMaximumRateHz) => new()
    {
        SchemaVersion = 2,
        Streaming = new CapabilityStreaming
        {
            MaximumSampleRateHz = BoardCeilingHz,
            CurrentMaximumRateHz = currentMaximumRateHz,
            RateValidation = "error"
        }
    };

    private static CapabilityRefreshingCoreDevice CoreDeviceRefreshingTo(int capHz)
    {
        var core = new CapabilityRefreshingCoreDevice("core")
        {
            RefreshedDocument = DocumentCapping(capHz)
        };
        core.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = BoardCeilingHz };
        return core;
    }

    /// <summary>
    /// The app's wrapper holding <paramref name="rateHz"/>, with <paramref name="core"/> already
    /// carrying it — the state both handoffs reach after
    /// <c>coreDevice.StreamingFrequency = StreamingFrequency</c>.
    /// </summary>
    private static TestDevice WrapperAt(int rateHz, DaqifiStreamingDevice core)
    {
        var device = new TestDevice();
        device.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = BoardCeilingHz };
        device.StreamingFrequency = rateHz;
        device.AttachCore(core);
        core.StreamingFrequency = rateHz;

        // Guard the arrangement: a wrapper that never took the rate would make every assertion
        // below pass for the wrong reason.
        Assert.Equal(rateHz, device.StreamingFrequency);
        return device;
    }

    private static Harness ConnectedCoreDevice(int? cappingTo = null)
    {
        var transport = new CapturingTransport();
        var core = new CapabilityRefreshingCoreDevice("core", transport)
        {
            RefreshedDocument = cappingTo is { } capHz ? DocumentCapping(capHz) : null
        };
        core.Connect();
        core.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = BoardCeilingHz };
        return new Harness(transport, core);
    }

    private sealed class Harness(CapturingTransport transport, CapabilityRefreshingCoreDevice coreDevice)
        : IDisposable
    {
        public CapturingTransport Transport { get; } = transport;

        public CapabilityRefreshingCoreDevice CoreDevice { get; } = coreDevice;

        public void Dispose()
        {
            // Release the parked reader before Core tears the session down: its consumer-thread
            // join is bounded, but leaving the thread blocked spends that whole bound on every
            // test. Closing the stream (rather than the transport) also avoids raising a
            // transport-level disconnect that Core would see as a drop rather than a teardown.
            Transport.CloseStream();
            CoreDevice.Dispose();
            Transport.Dispose();
        }
    }

    /// <summary>
    /// A real <see cref="DaqifiStreamingDevice"/> whose capability-document read answers with a
    /// document instead of talking to a board. Everything the cap is computed from — the merge
    /// onto <c>Metadata</c>, <c>SampleRateCap</c>, the enforcement — is Core's own.
    /// </summary>
    private sealed class CapabilityRefreshingCoreDevice : DaqifiStreamingDevice
    {
        public CapabilityRefreshingCoreDevice(string name)
            : base(name)
        {
        }

        public CapabilityRefreshingCoreDevice(string name, IStreamTransport transport)
            : base(name, transport, NullLogger.Instance)
        {
        }

        /// <summary>The document the refresh installs, or <c>null</c> to install none.</summary>
        public CapabilityDocument? RefreshedDocument { get; init; }

        /// <summary>What the refresh throws instead of answering, if anything.</summary>
        public Exception? RefreshFailure { get; init; }

        public int RefreshCount { get; private set; }

        public override Task<CapabilityDocument?> ReadCapabilityDocumentAsync(
            CancellationToken cancellationToken = default)
        {
            RefreshCount++;

            if (RefreshFailure != null)
            {
                throw RefreshFailure;
            }

            if (RefreshedDocument != null)
            {
                Metadata.ApplyCapabilityDocument(RefreshedDocument);
            }

            return Task.FromResult(RefreshedDocument);
        }
    }

    /// <summary>
    /// Minimal concrete <see cref="AbstractStreamingDevice"/>. Only two members need supplying,
    /// and the wrapper's own <c>SendMessage</c> is never reached: the traffic these cases read is
    /// what Core puts on the transport.
    /// </summary>
    private sealed class TestDevice : AbstractStreamingDevice
    {
        public override ConnectionType ConnectionType => ConnectionType.Usb;

        protected override void SendMessage(IOutboundMessage<string> message)
        {
        }

        /// <summary>Stands in for what <c>Connect()</c> assigns after opening the transport.</summary>
        public void AttachCore(DaqifiStreamingDevice coreDevice) => CoreDevice = coreDevice;

        /// <summary>
        /// The cap step both acquisition paths run immediately after handing the rate to Core.
        /// Reached the same way the base class's own subclasses reach it.
        /// </summary>
        public void HoldRateForHandoff(DaqifiStreamingDevice coreDevice) =>
            HoldRateToCurrentConfigurationCap(coreDevice);
    }
    #endregion
}
