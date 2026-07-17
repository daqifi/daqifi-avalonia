// ============================================================================
// EXPERIMENTAL — UNVALIDATED ON HARDWARE.
//
// An IStreamTransport over an Android USB-host CDC-ACM (virtual serial) link to
// a DAQiFi device. This is the phone->DAQiFi (OTG) sibling of Core's
// SerialStreamTransport: it exposes a Stream whose reads/writes are Android bulk
// transfers on the CDC DATA interface's bulk IN/OUT endpoints, and it asserts the
// CDC line-state DTR so the device begins streaming (the desktop's hardest-won USB
// detail — see SerialStreamingDevice / mobile-usb-feasibility.md §2).
//
// The following MUST be validated on a real phone + device (see
// docs/mobile-usb-feasibility.md §"Must be validated on device"):
//   * CDC endpoint selection (that the DATA interface + its bulk IN/OUT are the
//     ones picked here, and the comms-interface index used for the DTR wIndex).
//   * The DTR SET_CONTROL_LINE_STATE control transfer actually starts the stream.
//   * Bulk-IN read/write buffering + timeout behavior sustains the sample rate
//     without host overrun.
//   * The permission grant + attach flow (in UsbDeviceConnector).
//   * SD round-trip (SD:LIST? / SD:GET / __END_OF_FILE__) over this transport.
//
// NOTE on the namespace shadow: inside a `Daqifi.Avalonia.Android` namespace the
// root `Android.*` namespace is shadowed, so every Android type is referenced
// UNqualified via the usings below — never write `Android.`-qualified names here
// (see the note in MainActivity.cs).
// ============================================================================

using Android.Hardware.Usb;
using Daqifi.Core.Communication.Transport;
using Daqifi.Desktop.Common.Loggers;

namespace Daqifi.Avalonia.Android.Usb;

/// <summary>
/// CDC-ACM <see cref="IStreamTransport"/> for a permission-granted Android <c>UsbDevice</c>.
/// </summary>
public sealed class AndroidUsbStreamTransport : IStreamTransport
{
    // USB CDC class control request to set the RS-232 line state (DTR/RTS) on the
    // Communications (ACM) interface. requestType 0x21 = Host->Device | Class | Interface.
    private const int RequestTypeClassInterfaceOut = 0x21;
    private const int SetControlLineState = 0x22;
    private const int DtrRtsAsserted = 0x0003; // bit0 = DTR, bit1 = RTS

    // Per-call bulk-IN timeout. The Core StreamMessageConsumer treats a 0-byte read as
    // "no data, wait briefly" (not EOF), so a finite timeout that returns 0 on no-data
    // keeps its read loop responsive without blocking teardown.
    private const int BulkReadTimeoutMs = 200;
    private const int BulkWriteTimeoutMs = 2000;
    private const int ControlTransferTimeoutMs = 2000;

    private readonly UsbManager _manager;
    private readonly UsbDevice _device;

    private UsbDeviceConnection? _connection;
    private UsbInterface? _dataInterface;
    private UsbInterface? _commInterface;
    private UsbEndpoint? _bulkIn;
    private UsbEndpoint? _bulkOut;
    private UsbCdcStream? _stream;
    private bool _connected;
    private bool _disposed;

    /// <summary>
    /// Creates the transport for an already-enumerated, permission-granted device.
    /// </summary>
    /// <param name="manager">The Android USB service.</param>
    /// <param name="device">The DAQiFi USB device (permission must already be granted).</param>
    public AndroidUsbStreamTransport(UsbManager manager, UsbDevice device)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _device = device ?? throw new ArgumentNullException(nameof(device));
    }

    /// <inheritdoc />
    public Stream Stream
    {
        get
        {
            var stream = _stream;
            if (!_connected || stream == null)
            {
                throw new TransportNotConnectedException(
                    $"Android USB transport is not connected ({ConnectionInfo}).");
            }
            return stream;
        }
    }

    /// <inheritdoc />
    public bool IsConnected => _connected && _connection != null;

    /// <inheritdoc />
    public string ConnectionInfo =>
        $"USB CDC: {_device.DeviceName} (VID 0x{_device.VendorId:X4} / PID 0x{_device.ProductId:X4})";

    /// <inheritdoc />
    public event EventHandler<TransportStatusEventArgs>? StatusChanged;

    /// <inheritdoc />
    public void Connect()
    {
        ThrowIfDisposed();
        if (_connected)
        {
            return;
        }

        try
        {
            // Locate the CDC DATA interface + its bulk IN/OUT endpoints, and the
            // Communications (ACM) interface whose index the DTR control transfer targets.
            ResolveInterfacesAndEndpoints();

            if (_dataInterface == null || _bulkIn == null || _bulkOut == null)
            {
                throw new InvalidOperationException(
                    "No CDC data interface with bulk IN and OUT endpoints was found on the USB device.");
            }

            var connection = _manager.OpenDevice(_device)
                ?? throw new InvalidOperationException("UsbManager.OpenDevice returned null (permission not granted?).");
            _connection = connection;

            // Claim the data interface (force:true detaches any kernel driver). Best-effort
            // claim of the comms interface too — some stacks require it for the class request.
            if (!connection.ClaimInterface(_dataInterface, true))
            {
                throw new InvalidOperationException("Failed to claim the CDC data interface.");
            }
            if (_commInterface != null)
            {
                connection.ClaimInterface(_commInterface, true);
            }

            AssertDtr(connection);

            _stream = new UsbCdcStream(connection, _bulkIn, _bulkOut);
            _connected = true;
            OnStatusChanged(true, null);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warning(ex, $"Failed to connect Android USB transport ({ConnectionInfo})");
            SafeTeardown();
            OnStatusChanged(false, ex);
            throw;
        }
    }

    /// <inheritdoc />
    public Task ConnectAsync() => ConnectAsync(null);

    /// <inheritdoc />
    public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
    {
        // Retry options are accepted for interface parity; the underlying open is synchronous
        // and cheap, so a single attempt is made (matching NoRetry semantics).
        Connect();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Disconnect()
    {
        if (!_connected)
        {
            return;
        }

        SafeTeardown();
        OnStatusChanged(false, null);
    }

    /// <inheritdoc />
    public Task DisconnectAsync()
    {
        Disconnect();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            SafeTeardown();
        }
        catch
        {
            // Ignore errors during disposal.
        }

        _disposed = true;
    }

    private void ResolveInterfacesAndEndpoints()
    {
        for (var i = 0; i < _device.InterfaceCount; i++)
        {
            var iface = _device.GetInterface(i);

            if (iface.InterfaceClass == UsbClass.Comm)
            {
                _commInterface ??= iface;
                continue;
            }

            if (iface.InterfaceClass != UsbClass.CdcData)
            {
                continue;
            }

            UsbEndpoint? bulkIn = null;
            UsbEndpoint? bulkOut = null;
            for (var e = 0; e < iface.EndpointCount; e++)
            {
                var ep = iface.GetEndpoint(e);
                if (ep.Type != UsbAddressing.XferBulk)
                {
                    continue;
                }

                if (ep.Direction == UsbAddressing.In)
                {
                    bulkIn ??= ep;
                }
                else if (ep.Direction == UsbAddressing.Out)
                {
                    bulkOut ??= ep;
                }
            }

            if (bulkIn != null && bulkOut != null)
            {
                _dataInterface = iface;
                _bulkIn = bulkIn;
                _bulkOut = bulkOut;
                // Keep scanning only if we still lack a comms interface for the DTR wIndex.
                if (_commInterface != null)
                {
                    break;
                }
            }
        }
    }

    private void AssertDtr(UsbDeviceConnection connection)
    {
        // wIndex is the Communications (ACM) interface number; fall back to 0 when the comms
        // interface could not be identified (single-interface stacks).
        var interfaceIndex = _commInterface?.Id ?? 0;

        var result = connection.ControlTransfer(
            (UsbAddressing)RequestTypeClassInterfaceOut,
            SetControlLineState,
            DtrRtsAsserted,
            interfaceIndex,
            null,
            0,
            ControlTransferTimeoutMs);

        if (result < 0)
        {
            AppLogger.Instance.Warning(
                $"DTR SET_CONTROL_LINE_STATE control transfer returned {result} on {ConnectionInfo}; " +
                "the device may not start streaming (needs on-device validation).");
        }
    }

    private void SafeTeardown()
    {
        _connected = false;
        _stream = null;

        var connection = _connection;
        _connection = null;
        if (connection == null)
        {
            return;
        }

        try
        {
            if (_dataInterface != null)
            {
                connection.ReleaseInterface(_dataInterface);
            }
            if (_commInterface != null)
            {
                connection.ReleaseInterface(_commInterface);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warning(ex, "Error releasing USB interfaces during teardown");
        }

        try
        {
            connection.Close();
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warning(ex, "Error closing USB connection during teardown");
        }
    }

    private void OnStatusChanged(bool isConnected, Exception? error)
    {
        StatusChanged?.Invoke(this, new TransportStatusEventArgs(isConnected, ConnectionInfo, error));
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(AndroidUsbStreamTransport));
        }
    }

    /// <summary>
    /// A <see cref="Stream"/> adapter over the CDC bulk endpoints. Reads drain the bulk-IN
    /// endpoint; writes push to the bulk-OUT endpoint. A read that times out with no data
    /// returns 0 (the Core consumer treats that as "wait", not EOF).
    /// </summary>
    private sealed class UsbCdcStream : Stream
    {
        private readonly UsbDeviceConnection _connection;
        private readonly UsbEndpoint _in;
        private readonly UsbEndpoint _out;

        public UsbCdcStream(UsbDeviceConnection connection, UsbEndpoint bulkIn, UsbEndpoint bulkOut)
        {
            _connection = connection;
            _in = bulkIn;
            _out = bulkOut;
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);
            var transferred = _connection.BulkTransfer(_in, buffer, offset, count, BulkReadTimeoutMs);
            // Negative => timeout / no data / transient error: report 0 so the consumer waits
            // and retries rather than treating it as end-of-stream.
            return transferred > 0 ? transferred : 0;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            ArgumentNullException.ThrowIfNull(buffer);

            // Bulk writes can transfer partially; loop until the whole slice is sent.
            var remaining = count;
            var position = offset;
            while (remaining > 0)
            {
                var transferred = _connection.BulkTransfer(_out, buffer, position, remaining, BulkWriteTimeoutMs);
                if (transferred <= 0)
                {
                    throw new IOException(
                        $"USB bulk-OUT transfer failed (returned {transferred}) with {remaining} of {count} bytes unsent.");
                }

                position += transferred;
                remaining -= transferred;
            }
        }

        public override void Flush()
        {
            // Bulk transfers are issued synchronously in Write; nothing is buffered here.
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
