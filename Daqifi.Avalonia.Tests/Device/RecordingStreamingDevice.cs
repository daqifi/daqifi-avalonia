using System.ComponentModel;
using Daqifi.Core.Device.Network;
using Daqifi.Core.Device.SdCard;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Models;
using ChannelDirection = Daqifi.Core.Channel.ChannelDirection;
using ConnectionType = Daqifi.Desktop.Device.ConnectionType;
using CoreDeviceErrorEventArgs = Daqifi.Core.Device.DeviceErrorEventArgs;
using CoreSendFailedEventArgs = Daqifi.Core.Communication.Producers.MessageSendFailedEventArgs<string>;
using DeviceType = Daqifi.Core.Device.DeviceType;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// An <see cref="IStreamingDevice"/> that records the logging commands it is given and can be told
/// to refuse any of them.
/// </summary>
/// <remarks>
/// <para>
/// The tests for issue #214 need to distinguish "the fan-out reached this device" from "the fan-out
/// stopped at the one before it", which is the whole of what the fix changes. No subclass of
/// <see cref="AbstractStreamingDevice"/> can answer that: its logging commands are non-virtual and
/// all four of them go through <c>GetConnectedCoreDevice</c>, so a double with no Core device
/// behind it refuses everything and a double with one would need a live transport.
/// </para>
/// <para>
/// So the devices that are supposed to SUCCEED are these, and the devices that are supposed to
/// REFUSE are real <see cref="DroppableTestDevice"/>s throwing from real production frames — the
/// refusal is the part that must not be simulated.
/// </para>
/// </remarks>
internal sealed class RecordingStreamingDevice : IStreamingDevice
{
    public RecordingStreamingDevice(string serialNumber = "SN-OK", ConnectionType connectionType = ConnectionType.Usb)
    {
        DeviceSerialNo = serialNumber;
        Name = serialNumber;
        ConnectionType = connectionType;
    }

    /// <summary>Logging commands this device accepted, in order.</summary>
    internal List<string> Commands { get; } = [];

    /// <summary>When set, the named command throws this instead of being recorded.</summary>
    internal Dictionary<string, Exception> Refusals { get; } = [];

    /// <summary>
    /// Runs when a command arrives, before it is recorded or refused. The one test that needs it
    /// uses it to disconnect this device — i.e. to remove it from the very collection the fan-out
    /// is walking, which is what a command surfacing as a lost connection really does.
    /// </summary>
    internal Action<string>? OnCommand { get; set; }

    private void Accept(string command)
    {
        OnCommand?.Invoke(command);

        if (Refusals.TryGetValue(command, out var refusal))
        {
            throw refusal;
        }

        Commands.Add(command);
    }

    #region The members these tests actually exercise
    public DeviceMode Mode { get; private set; }

    public ConnectionType ConnectionType { get; }

    public bool IsLoggingToSdCard { get; private set; }

    public void SwitchMode(DeviceMode newMode)
    {
        Accept($"SwitchMode({newMode})");
        Mode = newMode;
    }

    public void InitializeStreaming() => Accept("InitializeStreaming");

    public void StopStreaming() => Accept("StopStreaming");

    public void StartSdCardLogging()
    {
        Accept("StartSdCardLogging");
        IsLoggingToSdCard = true;
    }

    public void StopSdCardLogging()
    {
        Accept("StopSdCardLogging");
        IsLoggingToSdCard = false;
    }

    public string DeviceDisplayName => DeviceSerialNo;
    #endregion

    #region The rest of the interface, which no test here touches
#pragma warning disable CS0067 // Part of IDevice; nothing in these tests raises them.
    public event EventHandler<ConnectionLostEventArgs>? ConnectionLost;

    public event EventHandler<CoreDeviceErrorEventArgs>? ErrorOccurred;

    public event EventHandler<CoreSendFailedEventArgs>? SendFailed;

    public event PropertyChangedEventHandler? PropertyChanged;
#pragma warning restore CS0067

    public int Id { get; set; }

    public void Reboot() { }

    public string Name { get; set; }

    public bool IsConnected => true;

    public IReadOnlyList<SdCardFile> SdCardFiles => [];

    public SdCardLogFormat SdCardLogFormat { get; set; } = SdCardLogFormat.Protobuf;

    public string DevicePartNumber => string.Empty;

    public uint TimestampFrequency => 0;

    public NetworkConfiguration NetworkConfiguration { get; } = new();

    public string MacAddress { get; set; } = string.Empty;

    public string DeviceSerialNo { get; set; }

    public string DeviceVersion { get; set; } = string.Empty;

    public bool IsFirmwareOutdated { get; set; }

    public DeviceType DeviceType => DeviceType.Unknown;

    public bool HasWincWifiModule => false;

    public bool IsWifiFirmwareOutdated { get; set; }

    public string WifiFirmwareVersion { get; set; } = string.Empty;

    public string IpAddress { get; set; } = string.Empty;

    public int StreamingFrequency { get; set; } = 1;

    public string DisplayIdentifier => DeviceSerialNo;

    public string FriendlyName => string.Empty;

    public List<IChannel> DataChannels { get; set; } = [];

    public int PwmFrequencyHz { get; set; }

    public bool Connect() => true;

    public bool Disconnect() => true;

    public void Write(string text) { }

    public void RefreshSdCardFiles() { }

    public void UpdateSdCardFiles(List<SdCardFile> files) { }

    public void SetFriendlyName(string name) { }

    public void InitializeDeviceState() { }

    public void AddChannel(IChannel channel) { }

    public void RemoveChannel(IChannel channel) { }

    public void AddChannels(IEnumerable<IChannel> channels) { }

    public void RemoveAllChannels() { }

    public void SetChannelOutputValue(IChannel channel, double value) { }

    public void SetChannelDirection(IChannel channel, ChannelDirection direction) { }

    public void SetChannelPwmEnabled(IChannel channel, bool enabled) { }

    public void SetChannelPwmDutyCycle(IChannel channel, int dutyCyclePercent) { }

    public Task UpdateNetworkConfiguration() => Task.CompletedTask;

    public Task<SdCardDownloadResult> DownloadSdCardFileAsync(
        string fileName,
        IProgress<SdCardTransferProgress>? progress = null,
        CancellationToken ct = default) => throw new NotSupportedException();

    public Task DeleteSdCardFileAsync(string fileName, CancellationToken ct = default)
        => throw new NotSupportedException();
    #endregion
}
