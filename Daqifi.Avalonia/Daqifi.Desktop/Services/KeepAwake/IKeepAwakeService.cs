namespace Daqifi.Desktop.Services.KeepAwake;

/// <summary>
/// Keeps the machine awake while streaming (hal: keep_awake). One interface,
/// per-platform backends: SetThreadExecutionState today; IOPMAssertion /
/// D-Bus inhibit / WakeLock / IdleTimerDisabled slot in as heads land.
/// Replaces the upstream Win32 NativeMethods shim in AbstractStreamingDevice.
/// </summary>
public interface IKeepAwakeService
{
    /// <summary>
    /// Prevents system sleep until <see cref="AllowSleep"/> is called.
    /// Returns false when the platform refused the request.
    /// </summary>
    bool PreventSleep();

    /// <summary>
    /// Lets the system sleep again. Returns false when the platform refused.
    /// </summary>
    bool AllowSleep();
}
