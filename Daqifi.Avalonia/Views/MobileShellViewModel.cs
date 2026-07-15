using System.Collections.ObjectModel;
using System.Net;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daqifi.Core.Device.Discovery;
using Daqifi.Desktop.Device.WiFiDevice;

namespace Daqifi.Avalonia.Views;

/// <summary>
/// Mobile shell view-model: WiFi discovery + connect over the SAME ported
/// stack the desktop uses (Core WiFiDeviceFinder → DaqifiStreamingDevice).
/// Mobile is WiFi/TCP only by recorded divergence (DIV-UI-003) — no serial
/// finder here on purpose.
/// </summary>
public partial class MobileShellViewModel : ObservableObject, IDisposable
{
    private WiFiDeviceFinder? _finder;
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

    [RelayCommand]
    private void Scan()
    {
        if (IsScanning) { StopScan(); return; }
        Devices.Clear();
        Status = "Scanning (UDP 30303)…";
        IsScanning = true;

        _finder = new WiFiDeviceFinder(30303);
        _finder.DeviceDiscovered += OnDeviceDiscovered;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        var finder = _finder;
        _scanTask = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await finder.DiscoverAsync(token);
                    await Task.Delay(3000, token);
                }
            }
            catch (OperationCanceledException) { /* stop requested */ }
            catch (ObjectDisposedException) { /* finder disposed on stop */ }
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
        if (_finder != null)
        {
            _finder.DeviceDiscovered -= OnDeviceDiscovered;
            _finder.Dispose();
            _finder = null;
        }
        _cts?.Dispose();
        _cts = null;
        Status = Devices.Count == 0
            ? "Scan stopped — no devices found."
            : $"Scan stopped — {Devices.Count} device(s).";
    }

    private void OnDeviceDiscovered(object? sender, DeviceDiscoveredEventArgs e)
    {
        var info = e.DeviceInfo;
        Dispatcher.UIThread.Post(() =>
        {
            // dedupe by serial (fall back to IP for serial-less announces)
            var key = string.IsNullOrWhiteSpace(info.SerialNumber)
                ? info.IPAddress?.ToString() : info.SerialNumber;
            if (Devices.Any(d => d.Key == key)) { return; }
            Devices.Add(new MobileDeviceItem(this, info, key ?? info.Name));
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
            () => new DaqifiStreamingDevice(item.Info));

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
                Status = $"Connected: {device.Name}  •  SN {device.Metadata.SerialNumber ?? "?"}"
                       + $"  •  {device.Metadata.IpAddress}:{device.Port}"
                       + $"  •  FW {device.Metadata.FirmwareVersion ?? "?"}";
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

    public void Dispose()
    {
        if (IsScanning) { StopScan(); }
        var connected = _connected;
        _connected = null;
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

    public MobileDeviceItem(MobileShellViewModel owner, IDeviceInfo info, string key)
    {
        _owner = owner;
        Info = info;
        Key = key;
    }

    public IDeviceInfo Info { get; }
    public string Key { get; }
    public string Name => string.IsNullOrWhiteSpace(Info.Name) ? "DAQiFi Device" : Info.Name;
    public string Ip => Info.IPAddress?.ToString() ?? "?";
    public string Detail =>
        $"SN {Info.SerialNumber ?? "?"}  •  {Ip}:{Info.Port?.ToString() ?? "?"}"
        + (Info.IsPowerOn ? "" : "  •  power off");

    [RelayCommand]
    private Task Connect() => _owner.ConnectAsync(this);
}
