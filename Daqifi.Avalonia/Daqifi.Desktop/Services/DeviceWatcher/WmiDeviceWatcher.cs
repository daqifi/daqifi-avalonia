using System;
using System.Management;
using System.Runtime.Versioning;

namespace Daqifi.Desktop.Services.DeviceWatcher;

/// <summary>
/// Windows backend of the device_watcher mechanism: WMI
/// Win32_DeviceChangeEvent, extracted verbatim from the upstream
/// ConnectionManager constructor.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WmiDeviceWatcher : IDeviceWatcher
{
    private ManagementEventWatcher? _deviceRemovedWatcher;

    public event EventHandler? DeviceRemoved;

    public void Start()
    {
        if (_deviceRemovedWatcher != null)
        {
            return;
        }

        // EventType 3 is Device Removal
        var deviceRemovedQuery = new WqlEventQuery("SELECT * FROM Win32_DeviceChangeEvent WHERE EventType = 3");

        _deviceRemovedWatcher = new ManagementEventWatcher(deviceRemovedQuery);
        _deviceRemovedWatcher.EventArrived += OnEventArrived;
        _deviceRemovedWatcher.Start();
    }

    public void Stop()
    {
        _deviceRemovedWatcher?.Stop();
    }

    public void Dispose()
    {
        if (_deviceRemovedWatcher != null)
        {
            _deviceRemovedWatcher.EventArrived -= OnEventArrived;
            _deviceRemovedWatcher.Dispose();
            _deviceRemovedWatcher = null;
        }
    }

    private void OnEventArrived(object sender, EventArrivedEventArgs e)
        => DeviceRemoved?.Invoke(this, EventArgs.Empty);
}
