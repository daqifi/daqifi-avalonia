using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Net;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daqifi.Avalonia.Services;
using Daqifi.Desktop;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Device.WiFiDevice;
using ChannelType = Daqifi.Core.Channel.ChannelType;
// Disambiguates our logger's level enum from the Sentry.BreadcrumbLevel that the
// Sentry package brings into scope globally.
using BreadcrumbLevel = Daqifi.Desktop.Common.Loggers.BreadcrumbLevel;

namespace Daqifi.Avalonia.Views;

/// <summary>
/// Mobile shell view-model: WiFi discovery + connect over the SAME ported
/// stack the desktop uses (Core WiFiDeviceFinder → DaqifiStreamingDevice).
/// Mobile is WiFi/TCP only by recorded divergence (DIV-UI-003) — no serial
/// finder here on purpose.
/// </summary>
public partial class MobileShellViewModel : ObservableObject, IDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _scanTask;
    // Base type, not the concrete WiFi device: the Stream tab drives whichever
    // transport connected — a WiFi DaqifiStreamingDevice OR a USB UsbStreamingDevice
    // (both extend AbstractStreamingDevice, which carries the whole streaming API).
    private AbstractStreamingDevice? _connected;

    // Watchdog state — see CheckForSilentStream. Counted in render-timer polls (50 ms each)
    // rather than elapsed time, so the threshold tracks how often this code actually runs.
    private const int SilentPollsBeforeStreamDeclaredDead = 160;   // ~8 s
    private int _silentPolls;

    public ObservableCollection<MobileDeviceItem> Devices { get; } = [];

    [ObservableProperty]
    private string _status = "Tap Scan to find DAQiFi devices on this WiFi network.";

    // Manual entry: many consumer APs isolate wireless clients and drop the
    // UDP broadcast discovery uses — the desktop app has the same escape
    // hatch. Default TCP data port is 9760.
    [ObservableProperty]
    private string _manualIp = "";

    [ObservableProperty]
    private string _manualPort = "9760";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ScanCommand))]
    private bool _isScanning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChannelSelector))]
    [NotifyPropertyChangedFor(nameof(ShowDeviceList))]
    private bool _isConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowChannelSelector))]
    [NotifyPropertyChangedFor(nameof(ShowDeviceList))]
    private bool _isStreaming;

    // Middle-pane visibility (mutually exclusive by state): scan-result device
    // list before connecting, channel + rate selector once connected, live plot
    // while streaming.
    public bool ShowDeviceList => !IsConnected && !IsStreaming;
    public bool ShowChannelSelector => IsConnected && !IsStreaming;

    // Rolling per-channel buffers the LivePlot renders. Palette is the
    // desktop channel colors, cycled.
    public ObservableCollection<ChannelSeries> Series { get; } = [];
    // Keyed by channel NAME (stable "AI0".."AI15"), NOT instance: enabling
    // channels makes the device re-sync DataChannels with fresh instances,
    // so an instance-keyed map (or a PropertyChanged subscription on the
    // originals) would miss every sample. The render timer polls the CURRENT
    // DataChannels each tick — immune to that instance churn.
    private readonly Dictionary<string, ChannelSeries> _seriesByName = [];
    private readonly Dictionary<string, long> _lastTicksByName = [];
    private long _totalSamples;
    public long TotalSamples => _totalSamples;
    private static readonly uint[] Palette =
    [
        0xFF4FC3F7, 0xFFFFB74D, 0xFF81C784, 0xFFE57373,
        0xFFBA68C8, 0xFF4DD0E1, 0xFFFFD54F, 0xFFA1887F,
    ];

    // Per-channel stream selection — populated on connect from the device's
    // analog input channels; the user picks which to stream (all on by default).
    public ObservableCollection<ChannelToggle> Channels { get; } = [];

    // Shared sample rate (Hz) for every enabled channel; DAQiFi streams all
    // enabled channels at one rate. 100 Hz is the desktop default.
    public int[] AvailableRates { get; } = [10, 50, 100, 200, 500, 1000];

    [ObservableProperty]
    private int _sampleRate = 100;

    [RelayCommand]
    private void SelectAllChannels()
    {
        foreach (var c in Channels) { c.IsSelected = true; }
        AppLogger.Instance.AddBreadcrumb("ui", $"Tapped Select all — {Channels.Count} channel(s)");
    }

    [RelayCommand]
    private void SelectNoChannels()
    {
        foreach (var c in Channels) { c.IsSelected = false; }
        AppLogger.Instance.AddBreadcrumb("ui", "Tapped Select none");
    }

    [RelayCommand]
    private void Scan()
    {
        if (IsScanning)
        {
            AppLogger.Instance.AddBreadcrumb("ui", "Tapped Scan again — stopping discovery");
            StopScan();
            return;
        }
        Devices.Clear();
        Status = "Scanning (UDP 30303)…";
        IsScanning = true;
        AppLogger.Instance.AddBreadcrumb("ui", "Tapped Scan — discovery started");

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _scanTask = Task.Run(async () =>
        {
            // Hold the platform discovery scope for the whole sweep — on
            // Android this is the WifiManager MulticastLock that lets the
            // UDP replies through the WiFi power-save filter.
            using var scope = NetworkDiscoveryScope.Enter();
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await NativeWiFiDiscovery.DiscoverAsync(
                        TimeSpan.FromSeconds(3), OnDeviceFound, token);
                    await Task.Delay(1500, token);
                }
            }
            catch (OperationCanceledException) { /* stop requested */ }
            catch (Exception ex)
            {
                Dispatcher.UIThread.Post(() => Status = $"Scan failed: {ex.Message}");
            }
        }, token);
    }

    private void StopScan()
    {
        IsScanning = false;
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        Status = Devices.Count == 0
            ? "Scan stopped — no devices found."
            : $"Scan stopped — {Devices.Count} device(s).";
        AppLogger.Instance.AddBreadcrumb("discovery", $"Discovery stopped — {Devices.Count} device(s) found");
    }

    private void OnDeviceFound(DiscoveredDevice device)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var key = device.Ip.ToString();
            if (Devices.Any(d => d.Key == key)) { return; }
            Devices.Add(new MobileDeviceItem(this, device, key));
            Status = $"{Devices.Count} device(s) found — tap one to connect.";
        });
    }

    [RelayCommand]
    private Task ManualConnect()
    {
        if (!IPAddress.TryParse(ManualIp.Trim(), out var ip))
        {
            Status = $"'{ManualIp}' is not a valid IP address.";
            return Task.CompletedTask;
        }
        if (!int.TryParse(ManualPort.Trim(), out var port) || port < 1 || port > 65535)
        {
            Status = $"'{ManualPort}' is not a valid TCP port.";
            return Task.CompletedTask;
        }
        return ConnectCoreAsync(
            $"{ip}:{port}",
            () => new DaqifiStreamingDevice(ip, port, $"DAQiFi @ {ip}"));
    }

    // Experimental USB (OTG) connect. The affordance is shown only on a platform
    // that registered a USB-host connector (Android). Tapping enumerates the
    // attached DAQiFi, connects it over USB-CDC, and registers it with
    // ConnectionManager — which lights up SD offload in the Storage pane. Live
    // USB streaming in this tab additionally needs _connected generalized off the
    // WiFi device type; SD offload needs only the registration. See
    // docs/mobile-usb-feasibility.md.
    public bool IsUsbAvailable => MobileUsbConnector.IsAvailable;

    [RelayCommand]
    private async Task ConnectUsb()
    {
        Status = "Connecting via USB…";
        AppLogger.Instance.AddBreadcrumb("ui", "Tapped Connect via USB");
        var result = await MobileUsbConnector.ConnectAsync();
        if (result.Device != null)
        {
            // The connector already connected + registered the USB device; adopt it
            // as the shell's active device so the Stream tab drives it (pick channels,
            // stream, plot) exactly like a WiFi device.
            AdoptConnectedDevice(result.Device);
        }
        else
        {
            Status = result.Message;
        }
    }

    internal Task ConnectAsync(MobileDeviceItem item) =>
        ConnectCoreAsync(
            $"{item.Name} ({item.Ip})",
            () => new DaqifiStreamingDevice(item.Device.Ip, item.Device.Port, item.Name));

    private async Task ConnectCoreAsync(string label, Func<DaqifiStreamingDevice> factory)
    {
        Status = $"Connecting to {label}…";
        AppLogger.Instance.AddBreadcrumb("ui", $"Tapped Connect — {label}");
        try
        {
            // Do NOT tear down the current connection before the new one succeeds:
            // a failed attempt (e.g. a wrong manual IP) would otherwise leave the
            // previous device disconnected-but-still-adopted and IsConnected lying
            // true (adversarial audit). AdoptConnectedDevice replaces + unregisters
            // the previous device only on success; on failure the current connection
            // is left intact.
            var device = factory();
            var ok = await Task.Run(device.Connect);
            if (ok)
            {
                AdoptConnectedDevice(device);
            }
            else
            {
                AppLogger.Instance.AddBreadcrumb(
                    "device", $"Connect refused by {label}", BreadcrumbLevel.Warning);
                Status = $"Connect to {label} failed — see device log.";
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.AddBreadcrumb(
                "device", $"Connect to {label} threw: {ex.GetType().Name}", BreadcrumbLevel.Error);
            Status = $"Connect failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Adopt an ALREADY-CONNECTED device (WiFi or USB) as the shell's active device:
    /// replace any prior connection, publish it to ConnectionManager so the projected
    /// panes observe it, populate the per-channel picker, and flip to the connected
    /// state. Transport-agnostic — the caller ensures the device is connected.
    /// </summary>
    private void AdoptConnectedDevice(AbstractStreamingDevice device)
    {
        var previous = _connected;
        if (previous != null && !ReferenceEquals(previous, device))
        {
            previous.PropertyChanged -= OnConnectedDevicePropertyChanged;
            try { ConnectionManager.Instance.UnregisterConnectedDevice(previous); } catch { /* best-effort */ }
            // Disconnect off the UI thread — a wedged transport must not stall the UI
            // (matches Dispose's teardown).
            Task.Run(() => { try { previous.Disconnect(); } catch { /* best-effort */ } });
        }
        _connected = device;
        // Follow the device's own state instead of trusting the snapshot taken when streaming
        // started. Without this the shell keeps reporting a live stream after the socket dies,
        // because IsStreaming/Status below are set once and never revisited (#99).
        device.PropertyChanged -= OnConnectedDevicePropertyChanged;
        device.PropertyChanged += OnConnectedDevicePropertyChanged;
        // Publish into the shared registry so the projected panes (Storage / Channels)
        // observe this device. Best-effort + idempotent (a USB device the connector
        // already registered is a no-op here).
        try { ConnectionManager.Instance.RegisterConnectedDevice(device); } catch { /* best-effort */ }

        // Populate the per-channel selection list (all on by default) before flipping
        // IsConnected so the selector shows with data.
        var analog = device.DataChannels
            .Where(c => c.Type == ChannelType.Analog && !c.IsOutput)
            .OrderBy(c => c.Index)
            .ToList();
        Channels.Clear();
        foreach (var ch in analog)
        {
            Channels.Add(new ChannelToggle(ch.Name, ch.Index));
        }
        IsStreaming = false;
        IsConnected = true;
        Status = $"Connected: {device.Name}  •  SN {device.Metadata.SerialNumber ?? "?"}"
               + $"  •  FW {device.Metadata.FirmwareVersion ?? "?"}"
               + $"  •  {analog.Count} analog ch — pick channels + rate, then stream";
    }

    [RelayCommand]
    private void StreamToggle()
    {
        var device = _connected;
        if (device == null) { return; }
        if (IsStreaming)
        {
            AppLogger.Instance.AddBreadcrumb("ui", "Tapped Stop streaming");
            StopStream();
            return;
        }

        // Enable the analog INPUT channels the user selected (AddChannel sends
        // the EnableAdcChannels SCPI + marks it active), wire a rolling series
        // per channel, set the rate, and start streaming. Look each selected
        // name up in the CURRENT DataChannels (immune to instance churn).
        var selected = Channels.Where(c => c.IsSelected).Select(c => c.Name).ToHashSet();
        var analog = device.DataChannels
            .Where(c => c.Type == ChannelType.Analog && !c.IsOutput && selected.Contains(c.Name))
            .OrderBy(c => c.Index)
            .ToList();
        if (analog.Count == 0)
        {
            Status = "Select at least one channel to stream.";
            return;
        }

        AppLogger.Instance.AddBreadcrumb(
            "ui", $"Tapped Start streaming — {analog.Count} channel(s) at {SampleRate} Hz");

        // Power the acquisition subsystem before streaming — the documented
        // DAQiFi handshake (POWer:STATe 1 + channel enable + STR:START).
        // Our Core connect uses InitializeDevice=false, which can skip the
        // TurnDeviceOn step, so a device that associated on WiFi but never
        // powered its ADC front-end streams NO data (matching the bench
        // "streaming yields no data" symptom). Idempotent to re-send.
        // NOTE: Write() sends RAW bytes with no terminator (unlike the producer
        // path, which appends "\r\n"). The firmware SCPI parser is line-based, so
        // an unterminated command merges with the next write and BOTH are dropped
        // — include the terminator explicitly. On WiFi this Write is a caught
        // no-op; on USB it actually transmits (adversarial audit).
        try { device.Write("SYSTem:POWer:STATe 1\r\n"); }
        catch { /* best-effort; InitializeStreaming still gates on IsStreaming */ }

        Series.Clear();
        _seriesByName.Clear();
        _lastTicksByName.Clear();
        _totalSamples = 0;
        var i = 0;
        foreach (var channel in analog)
        {
            device.AddChannel(channel);
            var s = new ChannelSeries(channel.Name, Palette[i % Palette.Length], 600);
            Series.Add(s);
            _seriesByName[channel.Name] = s;
            i++;
        }

        try
        {
            device.StreamingFrequency = SampleRate;
            device.InitializeStreaming();
        }
        catch (Exception ex)
        {
            AppLogger.Instance.AddBreadcrumb(
                "streaming", $"Start streaming failed: {ex.GetType().Name}", BreadcrumbLevel.Error);
            Status = $"Start streaming failed: {ex.Message}";
            StopStream();
            return;
        }
        IsStreaming = device.IsStreaming;
        if (IsStreaming)
        {
            // Refresh the Sentry device context: the tags were set at connect time, when no
            // channel was enabled yet, so daqifi.active_channels reported 0 on events raised
            // mid-stream (observed on DAQIFI-DESKTOP-21).
            AppLogger.Instance.SetDeviceContext(
                device.DevicePartNumber, device.DeviceSerialNo, device.DeviceVersion,
                device.ConnectionType == ConnectionType.Usb ? "usb" : "wifi", analog.Count);
        }
        Status = IsStreaming
            ? $"Streaming {analog.Count} channel(s) @ {SampleRate} Hz"
            : "Device did not enter streaming.";
    }

    /// <summary>
    /// Pull the latest sample off each active analog channel into its series.
    /// Called on the UI thread by the render timer — reads the CURRENT
    /// DataChannels (immune to the enable-time instance re-sync) and appends
    /// only genuinely-new samples (dedup by the sample's device timestamp).
    /// </summary>
    public void PollActiveSamples()
    {
        var device = _connected;
        if (device == null || !IsStreaming) { return; }
        var samplesBefore = _totalSamples;
        var monitored = 0;
        foreach (var channel in device.DataChannels)
        {
            if (channel.Type != ChannelType.Analog || channel.IsOutput) { continue; }
            if (!channel.IsActive) { continue; }
            monitored++;
            var sample = channel.ActiveSample;
            if (sample == null) { continue; }
            if (!_seriesByName.TryGetValue(channel.Name, out var series)) { continue; }
            var ticks = sample.TimestampTicks;
            if (_lastTicksByName.TryGetValue(channel.Name, out var last) && last == ticks)
            {
                continue;   // same frame as last poll — don't double-count
            }
            _lastTicksByName[channel.Name] = ticks;
            series.Append(sample.Value);
            _totalSamples++;
        }

        // Only judge silence when there is something whose silence would mean anything. If no
        // active analog channel is being polled, _totalSamples cannot advance no matter how
        // healthy the transport is, and the watchdog would tear down a working connection — the
        // exact false-positive this design was supposed to avoid. Reset rather than merely skip,
        // so a stretch of unmonitored polls cannot bank silence toward a later trip.
        if (monitored > 0)
        {
            CheckForSilentStream(samplesBefore);
        }
        else
        {
            _silentPolls = 0;
        }
    }

    /// <summary>
    /// Declares the stream dead after a run of polls that produced no new samples.
    /// </summary>
    /// <remarks>
    /// Defence in depth behind the device-level signal, for the case Core cannot see. A TCP socket
    /// whose peer vanished without a FIN or RST stays "connected" locally until a write fails or
    /// keepalive expires, so no transport error is ever raised and
    /// <c>ConnectionStatus.Lost</c> never arrives — the app would sit reporting a live stream over a
    /// frozen plot indefinitely (#99).
    /// <para>
    /// Silence is unambiguous here because the device streams continuously once started: the lowest
    /// selectable rate still delivers samples far more often than this threshold. The poll runs on
    /// the 50 ms render timer, so the window below is about eight seconds — comfortably longer than
    /// any legitimate gap (a Wi-Fi hiccup, a GC pause, the app being briefly descheduled), and short
    /// enough that a user is not left staring at a dead plot.
    /// </para>
    /// <para>
    /// Counting polls rather than wall-clock time is deliberate. The threshold should track how
    /// often this code actually runs, not how much time passes — a wall-clock deadline would fire
    /// after any window in which the UI was not being driven, whether or not data was flowing.
    /// <para>
    /// The render timer <b>does</b> keep firing while the app is backgrounded — measured, ticks
    /// 1.000 s apart with no gap across a 45 s background window (#113). So this watchdog is armed
    /// during background too, and that is the behaviour we want:
    /// </para>
    /// <list type="bullet">
    /// <item>the foreground service keeps the socket alive, samples keep arriving, and the
    /// watchdog stays quiet — verified at 347 samples/s through 180 s with the device asleep;</item>
    /// <item>if the stream dies anyway (an OEM kills the service, the AP drops), samples stop and
    /// the watchdog tears the dead connection down. The user returns to "Tap Scan to reconnect"
    /// rather than a frozen plot, which is the whole point.</item>
    /// </list>
    /// </para>
    /// </remarks>
    private void CheckForSilentStream(long samplesBefore)
    {
        if (_totalSamples != samplesBefore)
        {
            _silentPolls = 0;
            return;
        }

        _silentPolls++;
        if (_silentPolls < SilentPollsBeforeStreamDeclaredDead) { return; }

        _silentPolls = 0;
        AppLogger.Instance.Warning(
            $"No samples arrived for {SilentPollsBeforeStreamDeclaredDead} consecutive polls while " +
            "streaming; treating the connection as dead.");

        // Drop the connection, not just the stream. A device that has been told to stream at
        // 10 Hz or more and delivers nothing for eight seconds is not usefully connected, and the
        // transport underneath cannot be restarted (#99) — offering "Start streaming" on it would
        // just fail silently and re-arm this watchdog. Returning to the device list gives the user
        // the one action that does work: scan and reconnect, which builds a fresh transport.
        HandleConnectionLost();
    }

    private void StopStream()
    {
        try { _connected?.StopStreaming(); }
        catch { /* best-effort */ }
        IsStreaming = false;
        _silentPolls = 0;
        if (IsConnected) { Status = "Streaming stopped."; }
    }

    /// <summary>
    /// Mirrors the connected device's own state into the shell.
    /// </summary>
    /// <remarks>
    /// The shell used to snapshot <see cref="IsStreaming"/> once when streaming started and never
    /// look again, so a socket that died underneath it left the UI reporting a live stream over a
    /// frozen plot (#99). The device is the authority on both flags; this keeps the shell in step.
    /// Raised on the UI thread by <c>AbstractStreamingDevice</c>, which marshals on our behalf.
    /// </remarks>
    private void OnConnectedDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!ReferenceEquals(sender, _connected)) { return; }

        switch (e.PropertyName)
        {
            case nameof(AbstractStreamingDevice.IsStreaming):
                if (IsStreaming && _connected is { IsStreaming: false })
                {
                    HandleStreamLost();
                }
                break;

            case nameof(AbstractStreamingDevice.IsConnected):
                if (IsConnected && _connected is { IsConnected: false })
                {
                    HandleConnectionLost();
                }
                break;
        }
    }

    /// <summary>
    /// The stream stopped without the user asking. Stop plotting and say so.
    /// </summary>
    private void HandleStreamLost()
    {
        IsStreaming = false;
        _silentPolls = 0;
        AppLogger.Instance.AddBreadcrumb(
            "streaming", "Stream lost — device stopped sending data", BreadcrumbLevel.Warning);
        Status = "Streaming stopped — the device stopped sending data. Reconnect to start again.";
    }

    /// <summary>
    /// The device dropped. Return the shell to the device-list state so the user has an obvious
    /// route back, rather than leaving a connected-looking screen wired to nothing.
    /// </summary>
    /// <remarks>
    /// The device is unregistered and torn down because a lost transport cannot be reused —
    /// restarting the stream on it produces no data (#99). A fresh scan-and-connect builds a new
    /// transport, which does work.
    /// </remarks>
    private void HandleConnectionLost()
    {
        var lost = _connected;
        var name = lost?.Name ?? "device";

        IsStreaming = false;
        IsConnected = false;
        _silentPolls = 0;
        _connected = null;

        if (lost != null)
        {
            lost.PropertyChanged -= OnConnectedDevicePropertyChanged;
            try { ConnectionManager.Instance.UnregisterConnectedDevice(lost); } catch { /* best-effort */ }
            Task.Run(() => { try { lost.Disconnect(); } catch { /* best-effort */ } });
        }

        AppLogger.Instance.AddBreadcrumb(
            "device", $"Connection lost: {name}", BreadcrumbLevel.Error);
        Status = $"Lost connection to {name}. Tap Scan to reconnect.";
    }

    public void Dispose()
    {
        if (IsScanning) { StopScan(); }
        if (IsStreaming) { StopStream(); }
        var connected = _connected;
        _connected = null;
        IsConnected = false;
        if (connected != null)
        {
            connected.PropertyChanged -= OnConnectedDevicePropertyChanged;
            try { ConnectionManager.Instance.UnregisterConnectedDevice(connected); }
            catch { /* bridge is best-effort */ }
            Task.Run(() =>
            {
                try { connected.Disconnect(); }
                catch { /* teardown best-effort */ }
            });
        }
    }
}

public partial class MobileDeviceItem : ObservableObject
{
    private readonly MobileShellViewModel _owner;

    public MobileDeviceItem(MobileShellViewModel owner, DiscoveredDevice device, string key)
    {
        _owner = owner;
        Device = device;
        Key = key;
    }

    public DiscoveredDevice Device { get; }
    public string Key { get; }
    public string Name => Device.Name ?? "DAQiFi Device";
    public string Ip => Device.Ip.ToString();
    public string Detail => $"{Ip}:{Device.Port}  •  tap to connect";

    [RelayCommand]
    private Task Connect() => _owner.ConnectAsync(this);
}

/// <summary>
/// One selectable analog input channel in the pre-stream picker. Bound to a
/// checkbox; <see cref="MobileShellViewModel.StreamToggle"/> enables only the
/// selected names. Keyed by stable name ("AI0".."AI15"), not instance.
/// </summary>
public partial class ChannelToggle : ObservableObject
{
    public ChannelToggle(string name, int index)
    {
        Name = name;
        Index = index;
    }

    public string Name { get; }
    public int Index { get; }

    [ObservableProperty]
    private bool _isSelected = true;
}
