using System;

namespace Daqifi.Desktop.Services.KeepAwake;

/// <summary>
/// Picks the platform backend for the keep_awake mechanism.
/// </summary>
public static class KeepAwakeServiceFactory
{
    public static IKeepAwakeService Create()
        => OperatingSystem.IsWindows()
            ? new WindowsKeepAwakeService()
            : new NoOpKeepAwakeService();
}
