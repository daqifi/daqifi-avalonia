using System.Diagnostics;
using System.Text;
using Daqifi.Core.Communication.Transport;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// An <see cref="IStreamTransport"/> that is always willing to connect and records everything
/// written to it. Reads park until disposal, so Core's consumer thread sits idle instead of
/// spinning on an end-of-stream it would otherwise hit immediately.
/// </summary>
/// <remarks>
/// Shared rather than duplicated: it lets a test drive a real <c>DaqifiStreamingDevice</c> through
/// the app's own device wrapper and then read what actually reached the wire. Two suites want that
/// — <see cref="AbstractStreamingDeviceChannelAdoptionTests"/> for the channel-set commands
/// connect sends, and <see cref="CurrentRateCapEnforcementTests"/> for the rate the start command
/// carries.
/// </remarks>
internal sealed class CapturingTransport : IStreamTransport
{
    private readonly CapturingStream _stream = new();

    public Stream Stream => _stream;

    public bool IsConnected { get; private set; }

    public string ConnectionInfo => "capturing-transport";

    public event EventHandler<TransportStatusEventArgs>? StatusChanged;

    public string SentText => _stream.WrittenText;

    public void ClearSent() => _stream.ClearWritten();

    /// <summary>Ends the parked read without reporting a transport-level disconnect.</summary>
    public void CloseStream() => _stream.Dispose();

    /// <summary>
    /// Polls for <paramref name="fragment"/> appearing on the wire. Core's producer flushes on
    /// its own thread, so the write is not visible the instant the send call returns.
    /// </summary>
    public bool WaitForSentText(string fragment, TimeSpan timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < timeout)
        {
            if (_stream.WrittenText.Contains(fragment, StringComparison.Ordinal))
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return _stream.WrittenText.Contains(fragment, StringComparison.Ordinal);
    }

    public Task ConnectAsync() => ConnectAsync(null);

    public Task ConnectAsync(ConnectionRetryOptions? retryOptions)
    {
        Connect();
        return Task.CompletedTask;
    }

    public Task DisconnectAsync()
    {
        Disconnect();
        return Task.CompletedTask;
    }

    public void Connect()
    {
        if (IsConnected)
        {
            return;
        }

        IsConnected = true;
        StatusChanged?.Invoke(this, new TransportStatusEventArgs(true, ConnectionInfo));
    }

    public void Disconnect()
    {
        if (!IsConnected)
        {
            return;
        }

        IsConnected = false;
        StatusChanged?.Invoke(this, new TransportStatusEventArgs(false, ConnectionInfo));
    }

    public void Dispose()
    {
        Disconnect();
        _stream.Dispose();
    }
}

/// <summary>
/// Write-capturing, read-parking stream. Not a loopback: nothing written is ever read back,
/// because no test using it needs the device to answer.
/// </summary>
internal sealed class CapturingStream : Stream
{
    private readonly Lock _gate = new();
    private readonly ManualResetEventSlim _closed = new(false);
    private readonly MemoryStream _written = new();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public string WrittenText
    {
        get
        {
            lock (_gate)
            {
                return Encoding.ASCII.GetString(_written.ToArray());
            }
        }
    }

    public void ClearWritten()
    {
        lock (_gate)
        {
            _written.SetLength(0);
        }
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // Park until the stream is closed, then report end-of-stream. Returning 0 straight
        // away would spin Core's consumer thread flat out for the life of the test.
        _closed.Wait();
        return 0;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await Task.Run(() => _closed.Wait(cancellationToken), cancellationToken).ConfigureAwait(false);
        return 0;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        lock (_gate)
        {
            _written.Write(buffer, offset, count);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _closed.Set();
        }

        base.Dispose(disposing);
    }
}
