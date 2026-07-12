namespace Daqifi.Desktop.Services.KeepAwake;

/// <summary>
/// Fallback backend for platforms without a keep-awake head yet
/// (IOPMAssertion / D-Bus inhibit / WakeLock / IdleTimerDisabled are
/// planned backends). Reports success so streaming never aborts over a
/// missing power assertion — matching upstream, where a failed
/// SetThreadExecutionState only logged a warning.
/// </summary>
public sealed class NoOpKeepAwakeService : IKeepAwakeService
{
    public bool PreventSleep() => true;

    public bool AllowSleep() => true;
}
