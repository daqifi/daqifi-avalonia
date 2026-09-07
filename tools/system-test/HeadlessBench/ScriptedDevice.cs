using System.ComponentModel;
using System.Runtime.CompilerServices;
using Daqifi.Core.Device.Network;
using Daqifi.Core.Device.SdCard;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Models;
using ChannelDirection = Daqifi.Core.Channel.ChannelDirection;
using CoreAnalogChannel = Daqifi.Core.Channel.AnalogChannel;
using CoreDeviceErrorEventArgs = Daqifi.Core.Device.DeviceErrorEventArgs;
using CoreSendFailedEventArgs = Daqifi.Core.Communication.Producers.MessageSendFailedEventArgs<string>;
using DeviceType = Daqifi.Core.Device.DeviceType;

/// <summary>
/// The device double the two <c>--scripted</c> states that are not about disk contents need:
/// <c>sd-empty</c> (an SD card that answers with nothing on it) and <c>drop-mid-stream</c> (a
/// transport that goes away while a logging session is running).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives in the rig rather than being shared with the test project.</b> #304 framed the
/// choice as "a shared <c>Daqifi.Avalonia.TestDoubles</c> library, or a copy of
/// <c>Daqifi.Avalonia.Tests/Device/RecordingStreamingDevice.cs</c> that drifts". Both of those
/// assume the rig has to REUSE the test project's double. It does not, and nothing here is a copy:
/// <see cref="IStreamingDevice"/>, <see cref="IChannel"/>, <see cref="AnalogChannel"/>,
/// <see cref="DataSample"/> and <c>ConnectionManager</c> are all public production API, so the rig
/// implements the interface directly. That is why this file needs no new project, no
/// <c>packages.lock.json</c>, no CI restore line and no <c>InternalsVisibleTo</c> — and why it
/// widens no production surface for the benefit of a test.
/// </para>
/// <para>
/// <c>RecordingStreamingDevice</c> would not have served either row anyway. It exists to answer
/// #214's question ("did the logging fan-out reach this device?"), so it hardcodes
/// <c>SdCardFiles =&gt; []</c> with a no-op <c>RefreshSdCardFiles</c> — an empty list that was never
/// published, which is not the same thing as a card that answered — and it declares
/// <c>ConnectionLost</c> only to suppress CS0067, never raising it. Sharing it would have meant
/// growing one double for two consumers that want different behaviour from the same members.
/// </para>
/// <para>
/// <b>Why not subclass <see cref="AbstractStreamingDevice"/></b>, the way
/// <c>DroppedDeviceTestDoubles</c> does: <c>RefreshSdCardFiles</c> there is non-virtual and goes
/// through <c>GetConnectedCoreDevice</c>, so a subclass with no Core device behind it throws
/// <c>DeviceNotConnectedException</c> and <c>sd-empty</c> would be measuring the classifier's error
/// path instead of an empty card. Implementing the interface is the only shape that lets a listing
/// legitimately come back empty.
/// </para>
/// <para>
/// <b>What is real and what is not.</b> The channels are the app's own <see cref="AnalogChannel"/>
/// wrapping Core's own <c>AnalogChannel</c>, so scaling, <c>ActiveSample</c> and
/// <c>NotifyChannelUpdated</c> are production code; the rig only decides what values arrive and
/// when. What is simulated is exactly the transport: <see cref="Connect"/> reports success without
/// opening anything, and <see cref="Drop"/> replays the state change a real transport reports when
/// the link goes away. Everything downstream of those two — <c>ConnectionManager.Connect</c>'s
/// duplicate check, registration and event wiring, and its <c>OnDeviceConnectionLost</c> teardown —
/// is the app.
/// </para>
/// <para>
/// The interface drifting out from under this file is caught by the compiler, because CI builds
/// this project (the <c>desktop</c> job in <c>.github/workflows/build.yml</c>) — the same argument
/// the csproj already makes for compiling the rig at all.
/// </para>
/// </remarks>
internal sealed class ScriptedDevice : IStreamingDevice
{
    public ScriptedDevice(string serialNumber, ConnectionType connectionType = ConnectionType.Usb)
    {
        DeviceSerialNo = serialNumber;
        Name = serialNumber;
        ConnectionType = connectionType;
    }

    // ------------------------------------------------------------------ the transport it fakes

    private bool _isConnected;

    public bool Connect()
    {
        _isConnected = true;
        OnPropertyChanged(nameof(IsConnected));
        return true;
    }

    public bool Disconnect()
    {
        _isConnected = false;
        OnPropertyChanged(nameof(IsConnected));
        return true;
    }

    public bool IsConnected => _isConnected;

    /// <summary>
    /// Replays what a real wrapper does when Core reports the transport gone: settle the device's
    /// own state FIRST, then raise. <c>AbstractStreamingDevice.OnCoreStatusChanged</c> has that
    /// ordering because Core has already moved the device off Connected by the time it reports the
    /// transition, so a subscriber reading <see cref="IsConnected"/> during teardown must see the
    /// settled value — and <c>ConnectionManager</c>'s teardown does read it, through
    /// <c>DeviceLogsViewModel</c>'s connectivity watch.
    /// </summary>
    /// <remarks>
    /// Raising more than once is allowed on purpose: a board that is unplugged reports the drop on
    /// more than one path (Core's status transition and the port-presence watcher), and "the
    /// notification appears once, not repeatedly" is the matrix's stated expectation for
    /// <c>CONN-LOST</c>. The rig has to be able to raise twice to check that.
    /// </remarks>
    public void Drop(string reason)
    {
        _isConnected = false;
        OnPropertyChanged(nameof(IsConnected));
        ConnectionLost?.Invoke(this, new ConnectionLostEventArgs(reason));
    }

    // ------------------------------------------------------------------ SD card

    private List<SdCardFile> _sdCardFiles = [];

    /// <summary>What the next <see cref="RefreshSdCardFiles"/> will publish. An empty list is the
    /// <c>sd-empty</c> state: a card that answered and had nothing on it.</summary>
    public List<SdCardFile> SdCardListing { get; set; } = [];

    /// <summary>How many times the app asked for a listing. <c>SD-LIST</c> asserts this rather
    /// than only the resulting count, because an empty list is also what the app shows when the
    /// request never went out at all.</summary>
    public int SdListingRequests { get; private set; }

    public IReadOnlyList<SdCardFile> SdCardFiles => _sdCardFiles.AsReadOnly();

    /// <summary>
    /// The device half of <c>DeviceLogsViewModel.RefreshFilesAsync</c>: it calls this on a pool
    /// thread and then reads <see cref="SdCardFiles"/> back, so publishing here is what the real
    /// wrapper does after Core's <c>GetSdCardFilesAsync</c> returns.
    /// </summary>
    public void RefreshSdCardFiles()
    {
        SdListingRequests++;
        UpdateSdCardFiles([.. SdCardListing]);
    }

    public void UpdateSdCardFiles(List<SdCardFile> files)
    {
        _sdCardFiles = files;
        OnPropertyChanged(nameof(SdCardFiles));
    }

    // ------------------------------------------------------------------ channels

    public List<IChannel> DataChannels { get; set; } = [];

    /// <summary>Adds an active analog input built the way the app builds one — its own
    /// <see cref="AnalogChannel"/> over Core's own channel — so everything the sample path touches
    /// below <c>ActiveSample</c> is production code.</summary>
    public AnalogChannel AddAnalogInput(int index)
    {
        var channel = new AnalogChannel(this, new CoreAnalogChannel(index)) { Name = $"AI{index}" };
        DataChannels.Add(channel);
        return channel;
    }

    /// <summary>Delivers one reading on <paramref name="channel"/> the way the streaming path
    /// does — by assigning <c>ActiveSample</c>, which is what raises <c>OnChannelUpdated</c> and
    /// therefore what feeds <c>LoggingManager</c>, the live plot and the database writer.</summary>
    public void PushSample(IChannel channel, DateTime timestamp, double value) =>
        channel.ActiveSample = new DataSample(this, channel, timestamp, value);

    // ------------------------------------------------------------------ the rest of the interface

#pragma warning disable CS0067 // Part of IDevice; only ConnectionLost is raised here.
    public event EventHandler<ConnectionLostEventArgs>? ConnectionLost;

    public event EventHandler<CoreDeviceErrorEventArgs>? ErrorOccurred;

    public event EventHandler<CoreSendFailedEventArgs>? SendFailed;
#pragma warning restore CS0067

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public int Id { get; set; }

    public string Name { get; set; }

    public DeviceMode Mode { get; private set; }

    public ConnectionType ConnectionType { get; }

    public bool IsLoggingToSdCard { get; private set; }

    public SdCardLogFormat SdCardLogFormat { get; set; } = SdCardLogFormat.Protobuf;

    public string DevicePartNumber => "Nq1";

    public uint TimestampFrequency => 42_000_000;

    public NetworkConfiguration NetworkConfiguration { get; } = new();

    public string MacAddress { get; set; } = string.Empty;

    public string DeviceSerialNo { get; set; }

    public string DeviceVersion { get; set; } = "0.0.0-scripted";

    public bool IsFirmwareOutdated { get; set; }

    public DeviceType DeviceType => DeviceType.Nyquist1;

    public bool HasWincWifiModule => false;

    public bool IsWifiFirmwareOutdated { get; set; }

    public string WifiFirmwareVersion { get; set; } = "Unknown";

    public string IpAddress { get; set; } = string.Empty;

    public int StreamingFrequency { get; set; } = 1;

    public int MaxStreamingFrequency => 1000;

    public string DisplayIdentifier => DeviceSerialNo;

    public string DeviceDisplayName => string.IsNullOrEmpty(FriendlyName) ? DeviceSerialNo : FriendlyName;

    public string FriendlyName => string.Empty;

    public int PwmFrequencyHz { get; set; }

    public void Reboot() { }

    public void SwitchMode(DeviceMode newMode) => Mode = newMode;

    public void StartSdCardLogging() => IsLoggingToSdCard = true;

    public void StopSdCardLogging() => IsLoggingToSdCard = false;

    public void SetFriendlyName(string name) { }

    public void InitializeStreaming() { }

    public void StopStreaming() { }

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
}
