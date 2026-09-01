// Ported from upstream Daqifi.Desktop/Device/AbstractStreamingDevice.Diagnostics.cs.
//
// DO NOT manually delete the `// @port:` markers — they link symbols back to
// the correspondence map.

using CoreDeviceErrorEventArgs = Daqifi.Core.Device.DeviceErrorEventArgs;
using CoreSendFailedEventArgs = Daqifi.Core.Communication.Producers.MessageSendFailedEventArgs<string>;
using CoreStreamingDevice = Daqifi.Core.Device.DaqifiStreamingDevice;

namespace Daqifi.Desktop.Device;

/// <summary>
/// Re-exposes Core's background-failure events (<c>ErrorOccurred</c>, <c>SendFailed</c>) on this
/// wrapper so <c>ConnectionManager</c> can route them to the app log with the right severity.
/// Without this the failures have nowhere to go at all: a read-loop fault, a decode failure, an
/// exhausted reconnect and a producer write that never reached the device are all indistinguishable
/// from a device that has simply stopped sending.
/// </summary>
/// <remarks>
/// <para>
/// Forwarding rather than passing the Core event object straight through is deliberate: handlers
/// receive <c>this</c> as the sender, so a log line can name the device the way the user sees it
/// (<see cref="DeviceDisplayName"/>) instead of the Core-internal object.
/// </para>
/// <para>
/// The Core subscription is attached on the first app subscriber and released on the last, and the
/// attached instance is remembered so the release always targets the same Core device even if
/// <c>CoreDevice</c> has since been replaced by a reconnect. This late binding is what lets the
/// wiring live entirely in this file: <c>CoreDevice</c> does not exist until <c>Connect()</c>
/// creates it, and the wrapper's existing <c>SubscribeCoreDeviceEvents</c> runs before any app
/// subscriber has appeared.
/// </para>
/// </remarks>
// @port: Daqifi.Desktop.Device.AbstractStreamingDevice (Diagnostics partial)
public abstract partial class AbstractStreamingDevice
{
    #region Private Fields
    /// <summary>
    /// Guards the handler lists and the attach/detach pair below. Core raises both events from
    /// background threads while <c>ConnectionManager</c> subscribes and unsubscribes from the UI
    /// thread, so the bookkeeping cannot be left to unsynchronized delegate assignment.
    /// </summary>
    private readonly object _diagnosticsSync = new();

    /// <summary>
    /// The Core device this wrapper's forwarding handlers are currently attached to, or null when
    /// nothing is attached. Held separately from <c>CoreDevice</c> so detaching cannot miss the
    /// instance it attached to.
    /// </summary>
    private CoreStreamingDevice? _diagnosticsSource;

    private EventHandler<CoreDeviceErrorEventArgs>? _errorOccurred;
    private EventHandler<CoreSendFailedEventArgs>? _sendFailed;
    #endregion

    #region Events
    /// <inheritdoc />
    // @port: Daqifi.Desktop.Device.AbstractStreamingDevice.ErrorOccurred
    public event EventHandler<CoreDeviceErrorEventArgs>? ErrorOccurred
    {
        add
        {
            if (value == null) { return; }

            lock (_diagnosticsSync)
            {
                AttachDiagnostics();
                _errorOccurred += value;
            }
        }
        remove
        {
            if (value == null) { return; }

            lock (_diagnosticsSync)
            {
                _errorOccurred -= value;
                DetachDiagnosticsIfUnobserved();
            }
        }
    }

    /// <inheritdoc />
    // @port: Daqifi.Desktop.Device.AbstractStreamingDevice.SendFailed
    public event EventHandler<CoreSendFailedEventArgs>? SendFailed
    {
        add
        {
            if (value == null) { return; }

            lock (_diagnosticsSync)
            {
                AttachDiagnostics();
                _sendFailed += value;
            }
        }
        remove
        {
            if (value == null) { return; }

            lock (_diagnosticsSync)
            {
                _sendFailed -= value;
                DetachDiagnosticsIfUnobserved();
            }
        }
    }
    #endregion

    #region Internal Methods
    /// <summary>
    /// Points the Core subscription at whatever <c>CoreDevice</c> is now, or releases it when there
    /// is no Core device (or no app subscriber) left to bridge. Called from <c>Connect</c> once the
    /// Core device exists, and from <c>CleanupConnection</c> once it has been disposed.
    /// </summary>
    /// <remarks>
    /// Without the <c>CleanupConnection</c> half, a wrapper whose subscriber is still attached
    /// keeps <see cref="_diagnosticsSource"/> pointing at the Core device that cleanup just
    /// disposed — the wrapper holds the disposed Core device, and through it the transport, for as
    /// long as the wrapper itself is reachable. Without the <c>Connect</c> half, a reconnect of the
    /// same wrapper would leave the subscription on the previous session's Core device and report
    /// nothing from the new one. Neither is upstream's shape; both matter here because this port's
    /// <c>Connect</c> is a template that can run more than once on one wrapper instance.
    /// </remarks>
    private void RebindDiagnostics()
    {
        lock (_diagnosticsSync)
        {
            if (_errorOccurred == null && _sendFailed == null)
            {
                DetachDiagnostics();
                return;
            }

            AttachDiagnostics();
        }
    }
    #endregion

    #region Private Methods
    /// <summary>
    /// Attaches this wrapper's forwarding handlers to the current Core device, moving them off any
    /// previously attached instance first. A no-op while the device is disconnected
    /// (<c>CoreDevice</c> is null) — there is no background pipeline to report failures yet.
    /// </summary>
    // @port: Daqifi.Desktop.Device.AbstractStreamingDevice.AttachDiagnostics
    private void AttachDiagnostics()
    {
        var coreDevice = CoreDevice;
        if (ReferenceEquals(_diagnosticsSource, coreDevice))
        {
            return;
        }

        DetachDiagnostics();

        if (coreDevice == null)
        {
            return;
        }

        coreDevice.ErrorOccurred += OnCoreErrorOccurred;
        coreDevice.SendFailed += OnCoreSendFailed;
        _diagnosticsSource = coreDevice;
    }

    /// <summary>
    /// Releases the Core subscription once no app subscriber is left, so a disconnected device's
    /// Core instance is not kept reporting into handlers nobody is listening with.
    /// </summary>
    // @port: Daqifi.Desktop.Device.AbstractStreamingDevice.DetachDiagnosticsIfUnobserved
    private void DetachDiagnosticsIfUnobserved()
    {
        if (_errorOccurred == null && _sendFailed == null)
        {
            DetachDiagnostics();
        }
    }

    // @port: Daqifi.Desktop.Device.AbstractStreamingDevice.DetachDiagnostics
    private void DetachDiagnostics()
    {
        if (_diagnosticsSource == null)
        {
            return;
        }

        _diagnosticsSource.ErrorOccurred -= OnCoreErrorOccurred;
        _diagnosticsSource.SendFailed -= OnCoreSendFailed;
        _diagnosticsSource = null;
    }

    // @port: Daqifi.Desktop.Device.AbstractStreamingDevice.OnCoreErrorOccurred
    private void OnCoreErrorOccurred(object? sender, CoreDeviceErrorEventArgs e)
    {
        _errorOccurred?.Invoke(this, e);
    }

    // @port: Daqifi.Desktop.Device.AbstractStreamingDevice.OnCoreSendFailed
    private void OnCoreSendFailed(object? sender, CoreSendFailedEventArgs e)
    {
        _sendFailed?.Invoke(this, e);
    }
    #endregion
}
