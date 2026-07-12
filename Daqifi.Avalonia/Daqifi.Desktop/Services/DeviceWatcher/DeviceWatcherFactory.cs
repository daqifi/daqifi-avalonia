using System;

namespace Daqifi.Desktop.Services.DeviceWatcher;

/// <summary>
/// Picks the platform backend for the ONE watcher instance the
/// device_watcher mechanism allows.
/// </summary>
public static class DeviceWatcherFactory
{
    public static IDeviceWatcher Create()
        => OperatingSystem.IsWindows()
            ? new WmiDeviceWatcher()
            : new NoOpDeviceWatcher();
}
