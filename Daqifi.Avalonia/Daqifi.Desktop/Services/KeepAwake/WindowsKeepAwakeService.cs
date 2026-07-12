using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Daqifi.Desktop.Services.KeepAwake;

/// <summary>
/// Windows backend of the keep_awake mechanism — the
/// SetThreadExecutionState P/Invoke moved here from the upstream
/// AbstractStreamingDevice NativeMethods shim.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsKeepAwakeService : IKeepAwakeService
{
    // Prevents the system from entering sleep or hibernation.
    private const uint EsSystemRequired = 0x00000001;
    // Informs the system that the state should remain in effect until another call resets it.
    private const uint EsContinuous = 0x80000000;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint SetThreadExecutionState(uint esFlags);

    // SetThreadExecutionState returns 0 on failure.
    public bool PreventSleep() => SetThreadExecutionState(EsContinuous | EsSystemRequired) != 0;

    public bool AllowSleep() => SetThreadExecutionState(EsContinuous) != 0;
}
