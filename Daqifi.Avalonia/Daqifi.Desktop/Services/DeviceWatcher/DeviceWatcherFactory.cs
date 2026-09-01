using System;

namespace Daqifi.Desktop.Services.DeviceWatcher;

/// <summary>
/// Picks the platform backend for the ONE watcher instance the
/// device_watcher mechanism allows.
/// </summary>
public static class DeviceWatcherFactory
{
    /// <summary>
    /// True on the platforms where a user can physically unplug a USB serial device, and where a
    /// silent watcher therefore means a real loss of function they should be told about. False on
    /// the mobile heads, which have no serial transport at all (WiFi/TCP only, DIV-UI-003) and so
    /// have nothing to detect — their <see cref="NoOpDeviceWatcher"/> is by design, not a
    /// degradation.
    /// </summary>
    /// <remarks>
    /// The mobile heads land in the "false" branch because the runtime's platform probes are
    /// mutually exclusive: <c>OperatingSystem.IsLinux()</c> is false on Android (which reports
    /// <c>IsAndroid()</c>) and <c>OperatingSystem.IsMacOS()</c> is false on iOS (which reports
    /// <c>IsIOS()</c>).
    /// </remarks>
    public static bool PlatformSupportsSerialHotplug
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    public static IDeviceWatcher Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WmiDeviceWatcher();
        }

        // macOS and Linux have serial hardware but no WMI. Polling the port table is the
        // POSIX-desktop backend the interface always anticipated (issue #90); before it existed,
        // ConnectionManager constructed WMI unconditionally and hotplug-removal detection was dead
        // on every non-Windows head.
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux())
        {
            return new SerialPortPollingDeviceWatcher();
        }

        return new NoOpDeviceWatcher();
    }
}
