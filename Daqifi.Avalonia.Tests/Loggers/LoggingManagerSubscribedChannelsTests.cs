using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Logger;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ChannelDirection = Daqifi.Core.Channel.ChannelDirection;
using ChannelType = Daqifi.Core.Channel.ChannelType;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Pins <see cref="LoggingManager"/>'s channel-subscription contract for the case that broke it:
/// two connected boards that both expose a channel called <c>AI0</c>.
///
/// <para>
/// Devices name their channels by index, so every DAQiFi board exposes <c>AI0</c>, <c>AI1</c>, and
/// so on. <see cref="AbstractChannel.Equals(object)"/> compares <c>Name</c> alone — it does not
/// look at the device — so any value-based collection operation over
/// <see cref="LoggingManager.SubscribedChannels"/> is ambiguous the moment a second board is
/// connected. <c>Unsubscribe</c> resolved its target with a device-qualified predicate but then
/// dropped it with a value-based <c>List.Remove</c>, which re-matched by name and removed
/// whichever same-named channel sat earliest in the list. Turning a channel off on the second
/// board deactivated the right one and removed the wrong one: the first board's channel vanished
/// from the Active Channels list and from the unplug sweep while still streaming, and the
/// deactivated entry it left behind could never be removed again (<c>Unsubscribe</c> filters on
/// <c>IsActive</c>, so every later attempt early-returns).
/// </para>
///
/// <para>
/// Every assertion here uses <see cref="Assert.Same"/>, never <see cref="Assert.Equal{T}(T,T)"/>:
/// the latter would compare with the very name-only equality under test and pass on either
/// channel, which is the whole failure mode. Same reason for <see cref="Assert.Single(System.Collections.IEnumerable)"/>
/// plus an identity check rather than a collection comparison.
/// </para>
///
/// <para>
/// This mirrors upstream's <c>LoggingManagerSubscribedChannelsTests</c>, which shipped alongside
/// the same fix in daqifi-desktop <c>60dc32b6</c>. One deliberate difference: upstream builds real
/// <c>AnalogChannel</c>s over a mocked <c>IStreamingDevice</c>, which needs a mocking package this
/// repo's test project does not carry. <see cref="TestChannel"/> below derives from the production
/// <see cref="AbstractChannel"/> instead, so it inherits the real name-only
/// <c>Equals</c>/<c>GetHashCode</c> rather than falling back to reference equality — the mechanism
/// under test is the production one either way, and
/// <see cref="Channel_equality_is_name_only_across_devices"/> asserts that premise directly so it
/// cannot rot silently.
/// </para>
/// </summary>
public class LoggingManagerSubscribedChannelsTests
{
    private const string DeviceA = "SERIAL-A";
    private const string DeviceB = "SERIAL-B";

    /// <summary>
    /// A concrete <see cref="AbstractChannel"/> with nothing but the abstract members filled in.
    /// It inherits the production equality, which is the point — see the class remarks.
    /// </summary>
    private sealed class TestChannel : AbstractChannel
    {
        private string _name;
        private bool _isActive;

        internal TestChannel(string name, string deviceSerialNo)
        {
            _name = name;
            DeviceSerialNo = deviceSerialNo;
        }

        public override string Name
        {
            get => _name;
            set => _name = value;
        }

        public override ChannelDirection Direction { get; set; } = ChannelDirection.Input;

        public override int Index => 0;

        public override ChannelType Type => ChannelType.Analog;

        public override bool IsActive
        {
            get => _isActive;
            set => _isActive = value;
        }

        public override bool IsDigital => false;

        public override bool IsAnalog => true;
    }

    /// <summary>
    /// Stands in for the EF context factory the production singleton resolves from
    /// <c>App.ServiceProvider</c>. It throws rather than returning a context: nothing on the
    /// subscribe/unsubscribe path may touch the database, and if that ever changes these tests
    /// should say so out loud instead of quietly opening a SQLite file.
    /// </summary>
    private sealed class UnusedContextFactory : IDbContextFactory<LoggingContext>
    {
        public LoggingContext CreateDbContext() =>
            throw new InvalidOperationException(
                "Channel subscription must not touch the database.");
    }

    /// <summary>
    /// A fresh manager per test — the production <c>Instance</c> is a process-wide singleton, and
    /// sharing subscription state between tests would make them order-dependent.
    /// </summary>
    private static LoggingManager NewManager() => new(new UnusedContextFactory());

    /// <summary>
    /// The regression. Device A subscribes <c>AI0</c> first, so it occupies the index a name-only
    /// removal lands on; unsubscribing device B's <c>AI0</c> must leave A's alone and still
    /// streaming.
    /// </summary>
    [Fact]
    public void Unsubscribe_removes_the_targeted_channel_when_a_same_named_channel_was_subscribed_first()
    {
        var manager = NewManager();
        var channelA = new TestChannel("AI0", DeviceA);
        var channelB = new TestChannel("AI0", DeviceB);
        manager.Subscribe(channelA);
        manager.Subscribe(channelB);

        manager.Unsubscribe(channelB);

        Assert.Single(manager.SubscribedChannels);
        Assert.Same(channelA, manager.SubscribedChannels[0]);
        Assert.True(channelA.IsActive, "The other device's channel must keep streaming.");
        Assert.False(channelB.IsActive, "The unsubscribed channel must be deactivated.");
    }

    /// <summary>
    /// The mirror ordering, which was already correct before the fix — the targeted channel was
    /// also the first name-match. A preservation control rather than a second catch: it proves the
    /// reference-based removal did not break the ordinary path.
    /// </summary>
    [Fact]
    public void Unsubscribe_removes_the_targeted_channel_when_it_is_itself_the_first_same_named_channel()
    {
        var manager = NewManager();
        var channelA = new TestChannel("AI0", DeviceA);
        var channelB = new TestChannel("AI0", DeviceB);
        manager.Subscribe(channelA);
        manager.Subscribe(channelB);

        manager.Unsubscribe(channelA);

        Assert.Single(manager.SubscribedChannels);
        Assert.Same(channelB, manager.SubscribedChannels[0]);
        Assert.True(channelB.IsActive);
        Assert.False(channelA.IsActive);
    }

    /// <summary>
    /// The stranded-entry consequence. With the wrong channel removed, the deactivated one left
    /// behind can never be removed — <c>Unsubscribe</c> filters on <c>IsActive</c> — so the list
    /// keeps a dead channel until <c>ClearChannelList</c> or an app restart.
    /// </summary>
    [Fact]
    public void Unsubscribe_empties_the_list_when_both_same_named_channels_are_unsubscribed()
    {
        var manager = NewManager();
        var channelA = new TestChannel("AI0", DeviceA);
        var channelB = new TestChannel("AI0", DeviceB);
        manager.Subscribe(channelA);
        manager.Subscribe(channelB);

        // Second device first — the order the disconnect sweep uses.
        manager.Unsubscribe(channelB);
        manager.Unsubscribe(channelA);

        Assert.Empty(manager.SubscribedChannels);
        Assert.False(channelA.IsActive);
        Assert.False(channelB.IsActive);
    }

    /// <summary>
    /// The single-device path, which is what a bench run with one board can actually exercise:
    /// removal by reference must still remove the only subscribed channel.
    /// </summary>
    [Fact]
    public void Unsubscribe_removes_the_only_subscribed_channel_on_a_single_device()
    {
        var manager = NewManager();
        var channel = new TestChannel("AI0", DeviceA);
        manager.Subscribe(channel);

        manager.Unsubscribe(channel);

        Assert.Empty(manager.SubscribedChannels);
        Assert.False(channel.IsActive);
    }

    /// <summary>
    /// Unsubscribing one board's channel must not disturb a different-named channel on the other
    /// board — the ordinary multi-device case, where names do not collide at all.
    /// </summary>
    [Fact]
    public void Unsubscribe_leaves_a_differently_named_channel_on_another_device_alone()
    {
        var manager = NewManager();
        var a0 = new TestChannel("AI0", DeviceA);
        var b1 = new TestChannel("AI1", DeviceB);
        manager.Subscribe(a0);
        manager.Subscribe(b1);

        manager.Unsubscribe(a0);

        Assert.Single(manager.SubscribedChannels);
        Assert.Same(b1, manager.SubscribedChannels[0]);
        Assert.True(b1.IsActive);
    }

    /// <summary>
    /// Guards the premise the tests above rest on: the production channel base really does compare
    /// by name alone, so a value-based removal from the subscribed list is genuinely ambiguous. If
    /// channel equality is ever made device-aware, this test fails and points at the removal
    /// comment in <c>Unsubscribe</c>.
    /// </summary>
    [Fact]
    public void Channel_equality_is_name_only_across_devices()
    {
        var channelA = new TestChannel("AI0", DeviceA);
        var channelB = new TestChannel("AI0", DeviceB);
        Assert.NotEqual(channelA.DeviceSerialNo, channelB.DeviceSerialNo);

        Assert.True(
            channelA.Equals(channelB),
            "Channel equality is name-only, which is why removal must go by reference.");
        Assert.True(
            EqualityComparer<IChannel>.Default.Equals(channelA, channelB),
            "List<IChannel>.Remove goes through the default comparer, which lands on that same equality.");
    }
}
