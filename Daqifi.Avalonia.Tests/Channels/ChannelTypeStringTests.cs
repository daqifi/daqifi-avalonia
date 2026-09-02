using Daqifi.Desktop.Channel;
using Xunit;
using ChannelDirection = Daqifi.Core.Channel.ChannelDirection;
using ChannelType = Daqifi.Core.Channel.ChannelType;

namespace Daqifi.Avalonia.Tests.Channels;

/// <summary>
/// Characterisation tests for <see cref="AbstractChannel.TypeString"/>, the "Analog Input" /
/// "Digital Output" label the channel tiles and the profiles drawer show.
///
/// <para>
/// The four <c>(IsAnalog, IsDigital)</c> combinations are all covered, including the two that no
/// production channel produces. That is the point: the property is written as two sequential
/// <c>if</c> statements rather than a choice, so which one wins when both flags are set is decided
/// by statement order rather than by anything the reader can see. These tests pin the current
/// answer so a rewrite has to preserve it deliberately.
/// </para>
/// </summary>
public class ChannelTypeStringTests
{
    /// <summary>
    /// A concrete <see cref="AbstractChannel"/> whose type flags are settable independently, so the
    /// impossible combinations can be exercised too. The real <see cref="AnalogChannel"/> and
    /// <see cref="DigitalChannel"/> hard-code them as a mutually exclusive pair and each needs a
    /// Core channel to construct.
    /// </summary>
    private sealed class FlagChannel(bool isAnalog, bool isDigital) : AbstractChannel
    {
        public override string Name { get; set; } = "AI0";

        public override ChannelDirection Direction { get; set; } = ChannelDirection.Input;

        public override int Index => 0;

        public override ChannelType Type => isAnalog ? ChannelType.Analog : ChannelType.Digital;

        public override bool IsActive { get; set; }

        public override bool IsDigital => isDigital;

        public override bool IsAnalog => isAnalog;
    }

    [Theory]
    [InlineData(ChannelDirection.Input, "Analog Input")]
    [InlineData(ChannelDirection.Output, "Analog Output")]
    public void An_analog_channel_is_labelled_analog(ChannelDirection direction, string expected)
    {
        var channel = new FlagChannel(isAnalog: true, isDigital: false) { Direction = direction };

        Assert.Equal(expected, channel.TypeString);
    }

    [Theory]
    [InlineData(ChannelDirection.Input, "Digital Input")]
    [InlineData(ChannelDirection.Output, "Digital Output")]
    public void A_digital_channel_is_labelled_digital(ChannelDirection direction, string expected)
    {
        var channel = new FlagChannel(isAnalog: false, isDigital: true) { Direction = direction };

        Assert.Equal(expected, channel.TypeString);
    }

    [Fact]
    public void A_channel_claiming_both_kinds_is_labelled_analog()
    {
        // Today's answer comes from the second `if` overwriting the first. Nothing in production
        // reaches it, but the rewrite must not flip it by accident.
        var channel = new FlagChannel(isAnalog: true, isDigital: true);

        Assert.Equal("Analog Input", channel.TypeString);
    }

    [Fact]
    public void A_channel_claiming_neither_kind_is_labelled_by_direction_alone()
    {
        var channel = new FlagChannel(isAnalog: false, isDigital: false);

        Assert.Equal("Input", channel.TypeString);
    }

    [Fact]
    public void An_unrecognised_direction_is_labelled_unknown()
    {
        var channel = new FlagChannel(isAnalog: true, isDigital: false)
        {
            Direction = (ChannelDirection)(-1)
        };

        Assert.Equal("Analog Unknown", channel.TypeString);
    }
}
