using Android.Content;
using Android.Net.Wifi;
using Daqifi.Avalonia.Services;

namespace Daqifi.Avalonia.Android;

// NOTE: `using` directives resolve `Android.*` from the root namespace, but
// inside method bodies the enclosing `Daqifi.Avalonia.Android` namespace
// shadows the `Android` root — so every type here is referenced UNqualified
// (Context, WifiManager) via the usings, never as `Android.Content.Context`.

/// <summary>
/// Holds a <c>WifiManager.MulticastLock</c> for the duration of a discovery
/// sweep so Android delivers the UDP broadcast replies instead of
/// power-save-filtering them. Reference-counted, so overlapping sweeps are
/// safe.
/// </summary>
internal sealed class MulticastDiscoveryScope : INetworkDiscoveryScope
{
    private readonly WifiManager? _wifi;

    public MulticastDiscoveryScope(Context context)
    {
        // ApplicationContext, not the activity — the WifiManager outlives
        // any single activity and holding the activity would leak it.
        _wifi = context.ApplicationContext?
            .GetSystemService(Context.WifiService) as WifiManager;
    }

    public IDisposable Enter()
    {
        var mlock = _wifi?.CreateMulticastLock("daqifi-discovery");
        if (mlock != null)
        {
            mlock.SetReferenceCounted(true);
            mlock.Acquire();
        }
        return new LockHandle(mlock);
    }

    private sealed class LockHandle : IDisposable
    {
        private WifiManager.MulticastLock? _lock;

        public LockHandle(WifiManager.MulticastLock? mlock) => _lock = mlock;

        public void Dispose()
        {
            var mlock = _lock;
            _lock = null;
            if (mlock is { IsHeld: true }) { mlock.Release(); }
            mlock?.Dispose();
        }
    }
}
