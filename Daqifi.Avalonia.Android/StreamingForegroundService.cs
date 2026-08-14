using Android.App;
using Android.Content;
using Android.Content.PM;
// WifiMode lives in Android.Net, NOT Android.Net.Wifi alongside WifiManager.
using Android.Net;
using Android.Net.Wifi;
using Android.OS;
using Android.Util;

namespace Daqifi.Avalonia.Android;

// NOTE: never write `Android.*`-qualified names inside this namespace — the enclosing
// `Daqifi.Avalonia.Android` namespace shadows the `Android` root namespace; unqualified
// names resolved via the usings above are safe.

/// <summary>
/// Keeps the process in the foreground for as long as a device is connected, so an
/// acquisition survives the app leaving the screen.
/// </summary>
/// <remarks>
/// <para>
/// Without this, the platform tears our TCP socket down shortly after the activity stops.
/// Measured on a Galaxy A16 (Android 16): with 16 channels streaming at 100 Hz, samples
/// arrive at ~340/s while foregrounded, but a 63 s background window yields only ~204/s
/// on average — full rate for roughly the first half-minute, then a hard stop that never
/// recovers. Samsung logs the precursor as <c>BBA2 setIsFg isFg = false; delayValue 3999ms</c>.
/// The native app saw the same thing surface as
/// <c>SocketException: Software caused connection abort</c> — the local stack closing the
/// socket, not the Nyquist hanging up (daqifi-android#73, ported from daqifi-android#87).
/// A process with a running foreground service is exempt from that restriction.
/// </para>
/// <para>
/// This service does NOT address a socket that dies for other reasons; noticing that is a
/// separate concern, handled by the transport-loss propagation and data-arrival watchdog
/// added for #99. Keeping the two apart matters — a foreground service cannot detect a dead
/// socket, and a watchdog cannot stop the platform killing a healthy one.
/// </para>
/// </remarks>
[Service(Exported = false, ForegroundServiceType = ForegroundService.TypeConnectedDevice)]
internal sealed class StreamingForegroundService : Service
{
    /// <summary>Intent extra carrying the notification body text.</summary>
    internal const string ExtraStatusText = "status_text";

    private const string LogTag = "DaqifiFgs";
    private const string ChannelId = "daqifi_streaming";
    private const int NotificationId = 1001;

    private WifiManager.WifiLock? _wifiLock;

    // Started, not bound — nothing needs to call into it.
    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var text = intent?.GetStringExtra(ExtraStatusText) ?? "Connected to a device";

        try
        {
            // Safe to call repeatedly: on an already-foreground service this just
            // replaces the notification, which is how the text stays current.
            StartForeground(NotificationId, BuildNotification(text), ForegroundService.TypeConnectedDevice);
        }
        catch (global::System.Exception ex)
        {
            // Never let notification trouble take down an acquisition. If the platform
            // refuses the promotion the connection is still live — it just becomes
            // vulnerable to the background teardown described above, so this is worth
            // a log line even though it is not fatal.
            Log.Error(LogTag, $"startForeground failed; streaming will not survive backgrounding: {ex.Message}");

            // Stop, do not limp on. The coordinator reaches us via StartForegroundService, and
            // from Android 8 the system REQUIRES a service started that way to call
            // startForeground within ~5 s or it kills the app with a
            // ForegroundServiceDidNotStartInTimeException. Staying alive unpromoted therefore
            // trades a lost background exemption for a guaranteed crash. Stopping keeps the
            // connection working — it just loses the exemption, which is the degradation the
            // catch was always meant to accept.
            StopSelf(startId);
            return StartCommandResult.NotSticky;
        }

        AcquireWifiLock();

        // The coordinator starts and stops this service explicitly from connection state,
        // so a redelivered intent after a process kill would assert a connection that no
        // longer exists. NotSticky lets it stay dead until something reconnects.
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        ReleaseWifiLock();
        base.OnDestroy();
    }

    /// <summary>
    /// Holds Wi-Fi awake while connected.
    /// </summary>
    /// <remarks>
    /// Belt and braces, not the primary mechanism. The foreground service alone was enough to
    /// carry 16 channels at 100 Hz through a 180 s window with the device verifiably asleep
    /// (<c>mWakefulness=Dozing</c>), at 347 samples/s against a 333/s foreground baseline —
    /// Wi-Fi never dropped and this lock was not even being acquired at the time. It is kept
    /// for the case the native app documented on its own hardware: a Nyquist soft AP carries
    /// no internet, and Android drops such a network in the background (~20 s after onStop
    /// there). That has not been reproduced here.
    /// <para>
    /// Requires WAKE_LOCK. Without it <c>Acquire</c> throws <c>SecurityException</c> and the
    /// lock silently does nothing while everything still appears to work — which is exactly
    /// what happened before the permission was added, and why the catch below logs.
    /// </para>
    /// <para>
    /// <c>FullHighPerf</c> is deprecated from API 29 and, on Android 16, asking for it does not
    /// get you it: <c>dumpsys wifi</c> books the resulting lock as FULL_LOW_LATENCY and the
    /// high-perf counter never moves. Verified by watching "Locks acquired/released ... full low
    /// latency" step 4→5 on acquire and 4→5 on release as this service started and was destroyed.
    /// So the mode argument is largely academic on current platforms — changing it to
    /// <c>FullLowLatency</c> would be honest but would not change behaviour, and keeping
    /// <c>FullHighPerf</c> preserves the intent on older platforms that still honour it.
    /// </para>
    /// <para>
    /// Consequence worth knowing: since the effective lock is low-latency, and low-latency is
    /// documented to engage only while the screen is on and the app is foregrounded, this lock
    /// is almost certainly doing nothing in the backgrounded case. The foreground service is
    /// what actually keeps a background acquisition alive — which is consistent with the 180 s
    /// screen-off run above having succeeded while the lock was failing to acquire entirely.
    /// </para>
    /// </remarks>
    private void AcquireWifiLock()
    {
        try
        {
            if (_wifiLock is null)
            {
                // ApplicationContext: the WifiManager outlives this service instance.
                if (ApplicationContext?.GetSystemService(WifiService) is not WifiManager wifi)
                {
                    Log.Warn(LogTag, "No WifiManager; continuing without a Wi-Fi lock.");
                    return;
                }

                _wifiLock = wifi.CreateWifiLock(WifiMode.FullHighPerf, "daqifi:streaming");
                if (_wifiLock is null)
                {
                    Log.Warn(LogTag, "CreateWifiLock returned null; continuing without a Wi-Fi lock.");
                    return;
                }

                // Not reference counted: acquire/release are driven by a single
                // connected-or-not decision, so pairing is by state, not by call count.
                _wifiLock.SetReferenceCounted(false);
            }

            if (!_wifiLock.IsHeld) { _wifiLock.Acquire(); }

            // Report what the platform actually did rather than what was asked for.
            // `dumpsys wifi` counts high-perf acquisitions and can disagree with IsHeld.
            Log.Info(LogTag, $"Wi-Fi lock requested (FullHighPerf); IsHeld={_wifiLock.IsHeld}");
        }
        catch (global::System.Exception ex)
        {
            // A missing lock degrades reliability but must never break an otherwise
            // working acquisition. It is logged rather than swallowed: silently losing
            // this is what made the lock's real behaviour hard to establish.
            Log.Warn(LogTag, $"Wi-Fi lock unavailable: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private void ReleaseWifiLock()
    {
        try
        {
            if (_wifiLock is { IsHeld: true }) { _wifiLock.Release(); }
            _wifiLock?.Dispose();
            _wifiLock = null;
        }
        catch (global::System.Exception ex)
        {
            // Same rationale as AcquireWifiLock.
            Log.Warn(LogTag, $"Wi-Fi lock release failed: {ex.Message}");
        }
    }

    private Notification BuildNotification(string text)
    {
        if (GetSystemService(NotificationService) is NotificationManager manager)
        {
            // Creating an existing channel is a no-op, so this is safe on every start.
            // Low importance: ongoing status, never worth a sound or a heads-up.
            var channel = new NotificationChannel(
                ChannelId, "Device connection", NotificationImportance.Low);
            channel.Description =
                "Shown while DAQiFi is connected to a device, so acquisition keeps running when the app is not on screen.";
            channel.SetShowBadge(false);
            manager.CreateNotificationChannel(channel);
        }

        // Typed Intent rather than a class name string: MainActivity's Java name is a
        // mangled crc64 hash that changes across SDK bumps, and hard-coding it would
        // silently break the tap target (same reasoning as the note in AndroidManifest.xml).
        var open = new Intent(this, typeof(MainActivity));
        open.SetFlags(ActivityFlags.NewTask | ActivityFlags.ClearTop);
        var contentIntent = PendingIntent.GetActivity(
            this, 0, open, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        return new Notification.Builder(this, ChannelId)
            .SetContentTitle("DAQiFi")
            .SetContentText(text)
            .SetSmallIcon(Resource.Drawable.ic_stat_daqifi)
            .SetOngoing(true)
            .SetContentIntent(contentIntent)
            .Build();
    }
}
