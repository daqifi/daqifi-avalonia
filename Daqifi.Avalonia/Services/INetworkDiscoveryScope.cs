namespace Daqifi.Avalonia.Services;

/// <summary>
/// Platform hook wrapping a UDP discovery sweep so the platform can hold
/// whatever OS resource discovery needs for its duration.
///
/// Android WiFi hardware POWER-SAVE FILTERS incoming broadcast/multicast
/// packets unless the app holds a <c>WifiManager.MulticastLock</c> — so
/// the DAQiFi UDP broadcast discovery replies never reach the app without
/// it (the desktop finder works because desktop OSes don't filter this
/// way). Desktop/mobile-without-WiFi register nothing → the no-op scope.
/// </summary>
public interface INetworkDiscoveryScope
{
    /// <summary>Enter a discovery sweep; dispose to release.</summary>
    IDisposable Enter();
}

/// <summary>
/// Static registration point: the platform head sets <see cref="Current"/>
/// at startup (before any view loads). Callers use <see cref="Enter"/>,
/// which is a no-op when nothing is registered.
/// </summary>
public static class NetworkDiscoveryScope
{
    private sealed class NoOp : IDisposable { public void Dispose() { } }
    private static readonly IDisposable NoOpScope = new NoOp();

    public static INetworkDiscoveryScope? Current { get; set; }

    public static IDisposable Enter() => Current?.Enter() ?? NoOpScope;
}
