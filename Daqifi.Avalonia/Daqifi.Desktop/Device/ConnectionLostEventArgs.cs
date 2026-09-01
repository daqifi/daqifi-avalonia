// Ported from upstream Daqifi.Desktop/Device/ConnectionLostEventArgs.cs.
//
// DO NOT manually delete the `// @port:` markers — they link symbols back to
// the correspondence map.

namespace Daqifi.Desktop.Device;

/// <summary>
/// Raised when a streaming device's underlying Core connection drops unexpectedly (reboot,
/// USB/CDC unplug, WiFi/TCP drop, firmware-flash re-enumeration) rather than through an
/// explicit, app-initiated <see cref="IDevice.Disconnect"/> call.
/// </summary>
// @port: Daqifi.Desktop.Device.ConnectionLostEventArgs
public class ConnectionLostEventArgs(string reason) : EventArgs
{
    /// <summary>Short, human-readable description of why the connection was lost.</summary>
    // @port: Daqifi.Desktop.Device.ConnectionLostEventArgs.Reason
    public string Reason { get; } = reason;
}
