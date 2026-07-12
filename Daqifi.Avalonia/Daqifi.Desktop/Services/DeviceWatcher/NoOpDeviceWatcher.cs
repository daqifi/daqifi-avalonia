using System;

namespace Daqifi.Desktop.Services.DeviceWatcher;

/// <summary>
/// Backend that never fires — the device_watcher mechanism's legal fallback
/// for platforms without a hotplug source yet (mobile has no serial at all;
/// udev-netlink / IOKit desktop backends slot in here when they land).
/// </summary>
public sealed class NoOpDeviceWatcher : IDeviceWatcher
{
    // Never raised — see the interface contract: consumers must tolerate a
    // backend that stays silent.
    public event EventHandler? DeviceRemoved;

    public void Start()
    {
    }

    public void Stop()
    {
    }

    public void Dispose()
    {
    }
}
