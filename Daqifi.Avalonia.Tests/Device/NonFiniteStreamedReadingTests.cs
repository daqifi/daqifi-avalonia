using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Logger;
using Xunit;
using ConnectionType = Daqifi.Desktop.Device.ConnectionType;
using CoreAnalogChannel = Daqifi.Core.Channel.AnalogChannel;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Issue #295: a streaming frame's pre-scaled analog floats reached the app unvalidated, and a
/// single non-finite one used to make the Log Summary flyout's Average read <c>NaN</c> for the
/// rest of the session.
///
/// <para>
/// The frames below carry <c>AnalogInDataFloat</c> because that is the payload with no upstream
/// validation — the integer payload is scaled through Core, which sanitizes the coefficients it
/// scales with. Which payload a board actually sends is a firmware decision (the bench board at
/// fw 3.7.2 sends the integer one over USB), which is why the guard is placed after the branch
/// rather than inside the float leg.
/// </para>
///
/// <para>
/// Core's per-channel statistics keep a running <c>ValueSum</c>, so the poisoning is permanent
/// rather than momentary — and <c>Min</c> and <c>Max</c> keep working, because every comparison
/// against a NaN is false. One column of one row goes wrong and everything beside it keeps
/// reading correctly, which is exactly what makes it look like a rendering fault rather than a
/// bad reading. That asymmetry is pinned below, because it is the reason the bug was invisible.
/// </para>
///
/// <para>
/// These tests drive the <em>real</em> decode path: a protobuf frame goes in through the same
/// <c>ProtobufProtocolHandler</c> the transport uses, and what comes out the far end is read off
/// the channel and off a <see cref="SummaryLogger"/> subscribed to it the way
/// <c>LoggingManager.HandleChannelUpdate</c> subscribes the app's real loggers. Nothing about the
/// guard is simulated.
/// </para>
/// </summary>
public class NonFiniteStreamedReadingTests
{
    /// <summary>
    /// A concrete <see cref="AbstractStreamingDevice"/> that can be handed a streaming frame
    /// without a transport behind it.
    /// </summary>
    /// <remarks>
    /// <c>HandleInboundMessage</c> is the base class's own seam for "a frame arrived" — the WiFi
    /// device already calls it — and Core's <c>ProtobufProtocolHandler.HandleAsync</c> routes
    /// synchronously and returns <c>Task.CompletedTask</c>, so a frame pushed in here is fully
    /// processed by the time the call returns. That is what makes these tests deterministic
    /// rather than a poll for something that may never appear.
    /// </remarks>
    private sealed class StreamingProbeDevice : AbstractStreamingDevice
    {
        internal StreamingProbeDevice()
        {
            Name = "USB probe";
            DeviceSerialNo = "SN-PROBE";

            // Wires the real protocol handler; the app calls this once a device is connected.
            InitializeDeviceState();

            // The stream gate ProcessStreamMessage sits behind. Set directly rather than through
            // InitializeStreaming, which needs a connected Core device this double has no business
            // owning — and which is also why the per-session reset of the discard tally is argued
            // in the PR rather than tested here.
            IsStreaming = true;
        }

        public override ConnectionType ConnectionType => ConnectionType.Usb;

        protected override void SendMessage(IOutboundMessage<string> message) =>
            throw new NotSupportedException();

        /// <summary>
        /// Stands in for <c>LoggingManager.HandleDeviceMessage</c>'s fan-out to the registered
        /// loggers. <c>DispatchDeviceMessage</c> is <c>protected virtual</c> for exactly this —
        /// its own doc says "so tests can intercept the dispatch without the application service
        /// provider", and the real singleton resolves a DbContext factory off <c>App</c>.
        /// </summary>
        internal Action<DeviceMessage>? DeviceMessageDispatched { get; set; }

        protected override void DispatchDeviceMessage(DeviceMessage deviceMessage) =>
            DeviceMessageDispatched?.Invoke(deviceMessage);

        internal AnalogChannel AddActiveAnalogChannel(int index)
        {
            var channel = new AnalogChannel(this, new CoreAnalogChannel(index))
            {
                Name = $"AI{index}",
                IsActive = true
            };

            DataChannels.Add(channel);
            return channel;
        }

        /// <summary>
        /// Delivers one streaming frame carrying firmware-supplied pre-scaled floats, one value
        /// per active analog channel in index order.
        /// </summary>
        internal void ReceiveFloatFrame(uint deviceTicks, params float[] preScaledVolts)
        {
            var message = new DaqifiOutMessage { MsgTimeStamp = deviceTicks };
            foreach (var volts in preScaledVolts)
            {
                message.AnalogInDataFloat.Add(volts);
            }

            HandleInboundMessage(new MessageReceivedEventArgs(new GenericInboundMessage<object>(message)));
        }
    }

    /// <summary>
    /// A summary logger fed from the device the way the app feeds it, on both legs: samples arrive
    /// via <c>AbstractChannel.OnChannelUpdated</c> (which <c>LoggingManager.HandleChannelUpdate</c>
    /// subscribes to and fans out from), and each frame's <see cref="DeviceMessage"/> arrives via
    /// the dispatch the device already makes at the end of every frame. Republishing on every
    /// device message so a test does not have to send a thousand frames to see the panel refresh.
    /// </summary>
    private static SummaryLogger SummaryFedBy(StreamingProbeDevice device)
    {
        var summary = new SummaryLogger { Enabled = true, SampleSize = 1 };

        foreach (var channel in device.DataChannels)
        {
            channel.OnChannelUpdated += (_, sample) => summary.Log(sample);
        }

        device.DeviceMessageDispatched = summary.Log;
        return summary;
    }

    /// <summary>The single channel row the flyout would render for the single reporting device.</summary>
    private static SummaryLogger.ChannelSummary TheRow(SummaryLogger summary) =>
        Assert.Single(Assert.Single(summary.Devices).Channels);

    /// <summary>
    /// The value the firmware sent is not a measurement, so it does not become the channel's
    /// reading. Nothing downstream ever sees it — this is the whole of the fix.
    /// </summary>
    [Fact]
    public void A_non_finite_reading_never_becomes_the_channel_s_value()
    {
        var device = new StreamingProbeDevice();
        var channel = device.AddActiveAnalogChannel(0);

        device.ReceiveFloatFrame(1_000, float.NaN);

        Assert.Null(channel.ActiveSample);
    }

    /// <summary>
    /// An infinity is refused for the same reason a NaN is: it poisons a running sum just as
    /// thoroughly and is no more a voltage.
    /// </summary>
    /// <remarks>
    /// Deliberately a different answer from <c>SessionSampleWriter</c>'s, which refuses only NaN
    /// because its question is what SQLite can store — and SQLite stores infinities fine. Two
    /// boundaries, two questions, two answers.
    /// </remarks>
    [Theory]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void An_infinite_reading_is_refused_like_a_NaN(float reading)
    {
        var device = new StreamingProbeDevice();
        var channel = device.AddActiveAnalogChannel(0);

        device.ReceiveFloatFrame(1_000, reading);

        Assert.Null(channel.ActiveSample);
    }

    /// <summary>
    /// One bad value costs one reading on one channel — not the frame, and not its neighbours.
    /// A guard that dropped the whole frame would turn a single broken channel into a device that
    /// records nothing.
    /// </summary>
    [Fact]
    public void A_non_finite_reading_costs_its_neighbours_in_the_same_frame_nothing()
    {
        var device = new StreamingProbeDevice();
        var first = device.AddActiveAnalogChannel(0);
        var broken = device.AddActiveAnalogChannel(1);
        var last = device.AddActiveAnalogChannel(2);

        device.ReceiveFloatFrame(1_000, 1.5f, float.NaN, 2.5f);

        Assert.Equal(1.5, first.ActiveSample!.Value, precision: 5);
        Assert.Null(broken.ActiveSample);
        Assert.Equal(2.5, last.ActiveSample!.Value, precision: 5);
    }

    /// <summary>
    /// A channel is refused only for as long as its readings are unusable. The frame after the
    /// bad one lands normally — the channel is not latched off.
    /// </summary>
    [Fact]
    public void A_channel_reports_again_the_moment_its_readings_do()
    {
        var device = new StreamingProbeDevice();
        var channel = device.AddActiveAnalogChannel(0);

        device.ReceiveFloatFrame(1_000, float.NaN);
        device.ReceiveFloatFrame(2_000, 3.25f);

        Assert.Equal(3.25, channel.ActiveSample!.Value, precision: 5);
    }

    /// <summary>
    /// The user-visible half, and the reason this issue exists: before the guard, the Average
    /// column of a channel that was otherwise reading perfectly went to <c>NaN</c> on the first
    /// bad frame and stayed there for the rest of the run.
    /// </summary>
    [Fact]
    public void The_summary_average_survives_a_non_finite_reading()
    {
        var device = new StreamingProbeDevice();
        var channel = device.AddActiveAnalogChannel(0);
        var summary = SummaryFedBy(device);

        device.ReceiveFloatFrame(1_000, 2.0f);
        device.ReceiveFloatFrame(2_000, float.NaN);
        device.ReceiveFloatFrame(3_000, 4.0f);

        var row = TheRow(summary);

        Assert.Equal(3.0, row.AverageValue, precision: 5);
        Assert.Equal(2, row.SampleCount);
    }

    /// <summary>
    /// Why the bug read as a rendering fault. Min and Max were never wrong — Core compares with
    /// <c>&lt;</c> and <c>&gt;</c>, and every comparison against a NaN is false — so the row kept
    /// three correct numbers beside the one that had gone permanently wrong.
    /// </summary>
    [Fact]
    public void The_extremes_beside_the_average_stay_correct_too()
    {
        var device = new StreamingProbeDevice();
        var channel = device.AddActiveAnalogChannel(0);
        var summary = SummaryFedBy(device);

        device.ReceiveFloatFrame(1_000, 2.0f);
        device.ReceiveFloatFrame(2_000, float.NaN);
        device.ReceiveFloatFrame(3_000, 4.0f);

        var row = TheRow(summary);

        Assert.Equal(2.0, row.MinValue, precision: 5);
        Assert.Equal(4.0, row.MaxValue, precision: 5);
    }

    /// <summary>
    /// The regression guard: refusing non-finite readings must not cost a good one. This one
    /// passes without the fix, and is here so a future widening of the guard cannot go unnoticed.
    /// </summary>
    [Fact]
    public void A_finite_reading_still_reaches_the_channel_and_the_summary()
    {
        var device = new StreamingProbeDevice();
        var channel = device.AddActiveAnalogChannel(0);
        var summary = SummaryFedBy(device);

        device.ReceiveFloatFrame(1_000, 1.25f);

        Assert.Equal(1.25, channel.ActiveSample!.Value, precision: 5);
        Assert.Equal(1.25, TheRow(summary).AverageValue, precision: 5);
    }

    /// <summary>
    /// The throttled warning counts discards <em>per channel</em>, so a channel that starts
    /// failing later still gets a first line of its own instead of being swallowed by a shared
    /// count that has already passed its next power of ten.
    /// </summary>
    /// <remarks>
    /// This reads the tally the warning's occurrence number is drawn from rather than the log
    /// line, because <c>AppLogger</c> is a process-wide singleton that discards its NLog logger in
    /// test mode — there is nothing to assert against. What the tally cannot show is that the line
    /// was written; what it can show, and what matters to the design, is that the bookkeeping is
    /// per channel.
    /// </remarks>
    [Fact]
    public void Each_channel_keeps_its_own_tally_of_discarded_readings()
    {
        var device = new StreamingProbeDevice();
        device.AddActiveAnalogChannel(0);
        device.AddActiveAnalogChannel(1);

        device.ReceiveFloatFrame(1_000, float.NaN, 1.0f);
        device.ReceiveFloatFrame(2_000, float.NaN, 1.0f);
        device.ReceiveFloatFrame(3_000, float.NaN, float.PositiveInfinity);

        Assert.Equal(3, device.DiscardedNonFiniteReadings(0));
        Assert.Equal(1, device.DiscardedNonFiniteReadings(1));
    }

    /// <summary>
    /// A channel that has never produced a bad reading has nothing to report, so the throttle
    /// starts every channel at occurrence 1 rather than inheriting another channel's count.
    /// </summary>
    [Fact]
    public void A_channel_with_no_bad_readings_has_nothing_tallied()
    {
        var device = new StreamingProbeDevice();
        device.AddActiveAnalogChannel(0);

        device.ReceiveFloatFrame(1_000, 1.0f);

        Assert.Equal(0, device.DiscardedNonFiniteReadings(0));
    }

    /// <summary>
    /// The throttle logs the first ten discards on a channel in full and only then thins to
    /// powers of ten.
    /// </summary>
    /// <remarks>
    /// The opening band is what keeps the accepted reset race from costing a channel its first
    /// line altogether. A handler that was already past the <c>IsStreaming</c> guard when a
    /// restart cleared the tally can increment afterwards, so the new session's first real
    /// discard can land as occurrence 2 — which powers of ten alone would have swallowed until
    /// occurrence 10. Narrowing this back to <c>IsPowerOfTen</c> reintroduces exactly that, and
    /// this is the test that stops it.
    /// </remarks>
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(9, true)]
    [InlineData(10, true)]
    [InlineData(11, false)]
    [InlineData(99, false)]
    [InlineData(100, true)]
    [InlineData(101, false)]
    [InlineData(1_000, true)]
    public void The_first_ten_discards_are_reported_and_then_only_powers_of_ten(long occurrence, bool reported)
    {
        Assert.Equal(reported, AbstractStreamingDevice.IsReportedDiscardOccurrence(occurrence));
    }
}
