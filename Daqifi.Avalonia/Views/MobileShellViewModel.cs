using System.Collections.ObjectModel;
using System.Net;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daqifi.Avalonia.Services;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Device.WiFiDevice;
using ChannelType = Daqifi.Core.Channel.ChannelType;

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
    private DaqifiStreamingDevice? _connected;

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
    private bool _isConnected;

    [ObservableProperty]
    private bool _isStreaming;

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

    [RelayCommand]
    private void Scan()
    {
        if (IsScanning) { StopScan(); return; }
        Devices.Clear();
        Status = "Scanning (UDP 30303)…";
        IsScanning = true;

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

    internal Task ConnectAsync(MobileDeviceItem item) =>
        ConnectCoreAsync(
            $"{item.Name} ({item.Ip})",
            () => new DaqifiStreamingDevice(item.Device.Ip, item.Device.Port, item.Name));

    private async Task ConnectCoreAsync(string label, Func<DaqifiStreamingDevice> factory)
    {
        Status = $"Connecting to {label}…";
        try
        {
            var previous = _connected;
            var device = factory();
            var ok = await Task.Run(() =>
            {
                previous?.Disconnect();
                return device.Connect();
            });
            if (ok)
            {
                _connected = device;
                IsConnected = true;
                var analog = device.DataChannels.Count(
                    c => c.Type == ChannelType.Analog && !c.IsOutput);
                Status = $"Connected: {device.Name}  •  SN {device.Metadata.SerialNumber ?? "?"}"
                       + $"  •  FW {device.Metadata.FirmwareVersion ?? "?"}"
                       + $"  •  {analog} analog ch";
            }
            else
            {
                Status = $"Connect to {label} failed — see device log.";
            }
        }
        catch (Exception ex)
        {
            Status = $"Connect failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private void StreamToggle()
    {
        var device = _connected;
        if (device == null) { return; }
        if (IsStreaming) { StopStream(); return; }

        // Enable every analog INPUT channel on the device (AddChannel sends
        // the EnableAdcChannels SCPI + marks it active), wire a rolling
        // series per channel, set the rate, and start streaming.
        var analog = device.DataChannels
            .Where(c => c.Type == ChannelType.Analog && !c.IsOutput)
            .ToList();
        if (analog.Count == 0)
        {
            Status = "No analog input channels to stream.";
            return;
        }

        // Power the acquisition subsystem before streaming — the documented
        // DAQiFi handshake (POWer:STATe 1 + channel enable + STR:START).
        // Our Core connect uses InitializeDevice=false, which can skip the
        // TurnDeviceOn step, so a device that associated on WiFi but never
        // powered its ADC front-end streams NO data (matching the bench
        // "streaming yields no data" symptom). Idempotent to re-send.
        try { device.Write("SYSTem:POWer:STATe 1"); }
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
            device.StreamingFrequency = 100;
            device.InitializeStreaming();
        }
        catch (Exception ex)
        {
            Status = $"Start streaming failed: {ex.Message}";
            StopStream();
            return;
        }
        IsStreaming = device.IsStreaming;
        Status = IsStreaming
            ? $"Streaming {analog.Count} analog channel(s) @ 100 Hz"
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
        foreach (var channel in device.DataChannels)
        {
            if (channel.Type != ChannelType.Analog || channel.IsOutput) { continue; }
            if (!channel.IsActive) { continue; }
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
    }

    private void StopStream()
    {
        try { _connected?.StopStreaming(); }
        catch { /* best-effort */ }
        IsStreaming = false;
        if (IsConnected) { Status = "Streaming stopped."; }
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
