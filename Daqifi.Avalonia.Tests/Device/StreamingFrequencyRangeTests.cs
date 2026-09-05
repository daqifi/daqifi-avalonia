using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Desktop.Device;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Pins the range <see cref="AbstractStreamingDevice.StreamingFrequency"/> will store.
///
/// The wrapper's value is not private state: every path that starts acquisition assigns it
/// straight onto the Core device (<c>InitializeStreaming</c> and <c>StartSdCardLogging</c> both
/// do <c>coreDevice.StreamingFrequency = StreamingFrequency</c>), and Core's setter is
/// validating — it throws <see cref="ArgumentOutOfRangeException"/> outside
/// <c>1..DeviceCapabilities.MaxSamplingRate</c>. So a rate the wrapper accepts but Core does not
/// is not a wrong number, it is a device that refuses to start recording at the moment the user
/// presses the button, with the cause several screens away from where the value was set.
///
/// The <c>Core_</c> tests below pin that downstream contract against the pinned Daqifi.Core
/// package rather than restating it in prose; the rest pin that the wrapper never stores a value
/// those tests say Core would reject.
/// </summary>
public class StreamingFrequencyRangeTests
{
    /// <summary>
    /// Minimal concrete <see cref="AbstractStreamingDevice"/>. The property under test is a
    /// plain store, so nothing here connects or sends.
    /// </summary>
    private sealed class TestDevice : AbstractStreamingDevice
    {
        public override ConnectionType ConnectionType => ConnectionType.Usb;

        protected override void SendMessage(IOutboundMessage<string> message) =>
            throw new NotSupportedException();
    }

    private static TestDevice DeviceAdvertising(int maxSamplingRate)
    {
        var device = new TestDevice();
        device.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = maxSamplingRate };
        return device;
    }

    // ---- What Core does with the value the wrapper hands it -------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void Core_rejects_a_rate_below_one(int rate)
    {
        var core = new DaqifiStreamingDevice("core");

        Assert.Throws<ArgumentOutOfRangeException>(() => core.StreamingFrequency = rate);
    }

    [Fact]
    public void Core_rejects_a_rate_above_the_advertised_maximum()
    {
        var core = new DaqifiStreamingDevice("core");
        core.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = 1000 };

        Assert.Throws<ArgumentOutOfRangeException>(() => core.StreamingFrequency = 1001);
    }

    [Fact]
    public void Core_accepts_a_rate_above_1000_when_the_device_advertises_it()
    {
        var core = new DaqifiStreamingDevice("core");
        core.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = 22000 };

        core.StreamingFrequency = 5000;

        // 1000 is the UI slider's ceiling and the board table's default, not a hardware limit:
        // a device that describes itself as faster is entitled to be driven faster. Pinning
        // this stops the wrapper's guard from being written as a hard-coded 1000.
        Assert.Equal(5000, core.StreamingFrequency);
    }

    // ---- What the wrapper stores ----------------------------------------------------------

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(500)]
    [InlineData(1000)]
    public void A_rate_inside_the_advertised_range_is_stored_unchanged(int rate)
    {
        var device = DeviceAdvertising(1000);

        device.StreamingFrequency = rate;

        Assert.Equal(rate, device.StreamingFrequency);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void A_rate_below_one_is_raised_to_one(int rate)
    {
        var device = DeviceAdvertising(1000);

        device.StreamingFrequency = rate;

        Assert.Equal(1, device.StreamingFrequency);
    }

    [Theory]
    [InlineData(1001)]
    [InlineData(22000)]
    [InlineData(int.MaxValue)]
    public void A_rate_above_the_advertised_maximum_is_lowered_to_it(int rate)
    {
        var device = DeviceAdvertising(1000);

        device.StreamingFrequency = rate;

        Assert.Equal(1000, device.StreamingFrequency);
    }

    [Fact]
    public void A_device_that_advertises_a_higher_ceiling_keeps_the_higher_rate()
    {
        var device = DeviceAdvertising(22000);

        device.StreamingFrequency = 5000;

        Assert.Equal(5000, device.StreamingFrequency);
    }

    [Fact]
    public void An_unusable_advertised_ceiling_still_leaves_one_hertz_settable()
    {
        // MaxSamplingRate is a mutable, unvalidated public field on Core's capabilities, so a
        // capability document can leave it at zero. Core sanitizes that ceiling to 1 rather than
        // producing an impossible "1..0" range; the wrapper has to agree, or it would store a
        // rate Core then rejects.
        var device = DeviceAdvertising(0);

        device.StreamingFrequency = 1;

        Assert.Equal(1, device.StreamingFrequency);
    }

    [Fact]
    public void The_default_rate_is_one()
    {
        Assert.Equal(1, new TestDevice().StreamingFrequency);
    }

    [Fact]
    public void Storing_a_new_rate_still_raises_PropertyChanged()
    {
        var device = DeviceAdvertising(1000);
        var raised = new List<string?>();
        device.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        device.StreamingFrequency = 250;

        Assert.Contains(nameof(AbstractStreamingDevice.StreamingFrequency), raised);
    }

    // ---- The two halves together ----------------------------------------------------------

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(-1, 1000)]
    [InlineData(int.MinValue, 1000)]
    [InlineData(1001, 1000)]
    [InlineData(int.MaxValue, 1000)]
    [InlineData(int.MaxValue, 22000)]
    [InlineData(0, 22000)]
    public void Whatever_the_wrapper_stores_is_a_rate_Core_will_accept(int rate, int maxSamplingRate)
    {
        var device = DeviceAdvertising(maxSamplingRate);
        var core = new DaqifiStreamingDevice("core");
        core.Metadata.Capabilities = new DeviceCapabilities { MaxSamplingRate = maxSamplingRate };

        device.StreamingFrequency = rate;

        // This is exactly what InitializeStreaming and StartSdCardLogging do with the stored
        // value. Before the guard, each of these rows threw here.
        core.StreamingFrequency = device.StreamingFrequency;

        Assert.Equal(device.StreamingFrequency, core.StreamingFrequency);
    }
}
