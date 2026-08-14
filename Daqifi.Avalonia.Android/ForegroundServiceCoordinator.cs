using System.ComponentModel;
using Android.Content;
using Android.OS;
using Daqifi.Desktop;
using Daqifi.Desktop.Device;

namespace Daqifi.Avalonia.Android;

// NOTE: never write `Android.*`-qualified names inside this namespace — the enclosing
// `Daqifi.Avalonia.Android` namespace shadows the `Android` root namespace; unqualified
// names resolved via the usings above are safe.

/// <summary>
/// Runs <see cref="StreamingForegroundService"/> for exactly as long as at least one device
/// is connected.
/// </summary>
/// <remarks>
/// Driven off <see cref="ConnectionManager"/>'s change notification rather than from the
/// connect/disconnect call sites, so a new way to connect cannot forget to keep the
/// foreground state in step. This lives in the Android head because the shared library is
/// platform-neutral and must not learn about Android services.
/// </remarks>
internal static class ForegroundServiceCoordinator
{
    private static Context? _context;
    private static bool _attached;
    private static bool _serviceRunning;

    /// <summary>
    /// Begins tracking connection state. Safe to call more than once.
    /// </summary>
    public static void Attach(Context context)
    {
        if (_attached) { return; }

        // ApplicationContext, not the activity: the subscription below outlives any single
        // activity, and holding the activity would pin a destroyed one.
        _context = context.ApplicationContext ?? context;
        ConnectionManager.Instance.PropertyChanged += OnConnectionManagerPropertyChanged;
        _attached = true;
    }

    private static void OnConnectionManagerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ConnectionManager.ConnectedDevices)) { return; }

        Sync();
    }

    /// <summary>
    /// Re-syncs when a device's own <c>IsConnected</c> flips.
    /// </summary>
    /// <remarks>
    /// The list-change subscription alone is not enough. <see cref="Sync"/> decides from each
    /// device's <c>IsConnected</c>, but <c>ConnectedDevices</c> only notifies on add/remove — so a
    /// device that drops while still listed (Core reports <c>ConnectionStatus.Lost</c>, and the
    /// owner has not torn it down yet, or never does) changed nothing this class was watching.
    /// The service and its Wi-Fi lock would then stay up indefinitely with no device attached:
    /// a battery drain plus an ongoing notification the user cannot dismiss.
    /// </remarks>
    private static void OnDevicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(IStreamingDevice.IsConnected)) { return; }

        Sync();
    }

    private static void Sync()
    {
        var context = _context;
        if (context is null) { return; }

        // Count what is actually connected rather than merely listed: a device mid-teardown
        // can still be in the collection.
        //
        // Re-subscribing here (rather than only on add) keeps the per-device hooks in step with
        // the current list without tracking membership separately: -= on a handler that is not
        // attached is a documented no-op, and += after it cannot double-subscribe.
        var connected = 0;
        foreach (var device in ConnectionManager.Instance.ConnectedDevices)
        {
            if (device is null) { continue; }
            device.PropertyChanged -= OnDevicePropertyChanged;
            device.PropertyChanged += OnDevicePropertyChanged;
            if (device.IsConnected) { connected++; }
        }

        if (connected == 0)
        {
            Stop(context);
            return;
        }

        // Deliberately reports connection, not streaming. IsStreaming lives on the concrete
        // device and raises no notification that reaches this class, so a "Streaming…" label
        // here would go stale the moment a stream stopped — which is precisely the failure
        // mode tracked in #99. Connection count is what gates this service and is always current.
        var text = connected == 1
            ? "Connected to 1 device"
            : $"Connected to {connected} devices";

        Start(context, text);
    }

    private static void Start(Context context, string statusText)
    {
        var intent = new Intent(context, typeof(StreamingForegroundService));
        intent.PutExtra(StreamingForegroundService.ExtraStatusText, statusText);

        try
        {
            context.StartForegroundService(intent);
            _serviceRunning = true;
        }
        catch (global::System.Exception)
        {
            // From Android 12 a foreground service cannot be started while the app is in the
            // background. Connecting is a user action taken on screen, so the normal path is
            // allowed; a background reconnect is not, and throwing here would fail the
            // reconnect itself. The connection still works — it just loses the exemption.
            _serviceRunning = false;
        }
    }

    private static void Stop(Context context)
    {
        if (!_serviceRunning) { return; }

        try
        {
            context.StopService(new Intent(context, typeof(StreamingForegroundService)));
        }
        catch (global::System.Exception)
        {
            // Nothing useful to do if teardown fails; the service stops with the process.
        }

        _serviceRunning = false;
    }
}
