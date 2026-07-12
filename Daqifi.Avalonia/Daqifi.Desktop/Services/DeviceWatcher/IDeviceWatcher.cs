using System;

namespace Daqifi.Desktop.Services.DeviceWatcher;

/// <summary>
/// One hotplug watcher interface with per-OS backends (hal: usb_hotplug_watch;
/// device_watcher mechanism). Exactly ONE instance feeds ConnectionManager;
/// platform backends register through it. A backend that never fires is legal
/// (mobile has no serial) — consumers must not rely on watcher events for
/// initial device discovery.
/// </summary>
public interface IDeviceWatcher : IDisposable
{
    /// <summary>
    /// Raised when a device (any hardware class) was removed. Mirrors the
    /// upstream Win32_DeviceChangeEvent EventType 3 subscription; arrival
    /// events can be added when a ported consumer needs them.
    /// </summary>
    event EventHandler? DeviceRemoved;

    void Start();

    void Stop();
}
