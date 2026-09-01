using Daqifi.Desktop.Common.Loggers;
using System.IO.Ports;

namespace Daqifi.Desktop.Services.DeviceWatcher;

/// <summary>
/// POSIX-desktop backend of the device_watcher mechanism (macOS and Linux). There is no WMI
/// there, so the serial port table is sampled instead and <see cref="DeviceRemoved"/> is raised
/// whenever a port that was present has gone away.
/// </summary>
/// <remarks>
/// <para>
/// This deliberately samples the SAME source the removal handler consumes —
/// <see cref="SerialPort.GetPortNames"/> — so the watcher can never report a removal that
/// <c>ConnectionManager.CheckIfSerialDeviceWasRemoved</c> then fails to see. On macOS that list
/// is the <c>/dev/cu.*</c> and <c>/dev/tty.*</c> nodes, which is also where discovery and
/// <c>SerialStreamingDevice</c> get their port names, so the two sides agree by construction.
/// </para>
/// <para>
/// Only shrinkage of the port set fires the event. Arrivals are ignored: the interface documents
/// <see cref="DeviceRemoved"/> as a removal signal, and discovery — not this watcher — owns
/// finding new devices. That also matches the Windows backend, which subscribes to
/// Win32_DeviceChangeEvent EventType 3 (removal) only.
/// </para>
/// <para>
/// A poll is a directory listing of <c>/dev</c>. At the default one-second cadence that is far
/// cheaper than the serial probing discovery already performs, and it stops entirely on
/// <see cref="Stop"/> / <see cref="Dispose"/>.
/// </para>
/// </remarks>
public sealed class SerialPortPollingDeviceWatcher : IDeviceWatcher
{
    #region Constants
    /// <summary>
    /// Sampling cadence. A user who unplugs a device sees the disconnect within this window; the
    /// Windows WMI backend is event-driven and has no equivalent latency.
    /// </summary>
    public static readonly TimeSpan DefaultPollInterval = TimeSpan.FromSeconds(1);
    #endregion

    #region Private Fields
    private readonly IAppLogger _logger;
    private readonly TimeSpan _pollInterval;
    private readonly Func<string[]> _portNameProvider;
    private readonly object _sync = new();

    private Timer? _timer;
    private HashSet<string>? _knownPorts;

    /// <summary>
    /// Identifies the current Start/Stop cycle. Each timer callback carries the generation it was
    /// created for, so a tick still enumerating when the watcher is stopped and restarted cannot
    /// compare its stale reading against the new baseline and report a removal that never happened.
    /// </summary>
    private int _generation;

    /// <summary>Guards against a slow poll overlapping the next timer tick.</summary>
    private int _pollInProgress;

    /// <summary>
    /// Enumeration failures are logged once per outage rather than once per tick, so a permissions
    /// problem on <c>/dev</c> cannot flood the log. Only ever touched from inside a poll (or from
    /// <see cref="Start"/> before the timer exists), and the reentrancy gate serialises those, so
    /// the worst a race could cost is one duplicate warning.
    /// </summary>
    private bool _enumerationFailureLogged;

    private bool _disposed;
    #endregion

    /// <summary>Creates the watcher with the production port source and cadence.</summary>
    public SerialPortPollingDeviceWatcher()
        : this(null, null, null)
    {
    }

    /// <param name="logger">Application logger; null uses <c>AppLogger.Instance</c>.</param>
    /// <param name="pollInterval">
    /// Sampling cadence; null uses <see cref="DefaultPollInterval"/>. Must be positive.
    /// </param>
    /// <param name="portNameProvider">
    /// Source of the current port table; null uses <see cref="SerialPort.GetPortNames"/>.
    /// Overridable so the polling logic can be driven without real hardware.
    /// </param>
    public SerialPortPollingDeviceWatcher(
        IAppLogger? logger,
        TimeSpan? pollInterval = null,
        Func<string[]>? portNameProvider = null)
    {
        if (pollInterval is { } interval && interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pollInterval), interval, "Poll interval must be positive.");
        }

        _logger = logger ?? AppLogger.Instance;
        _pollInterval = pollInterval ?? DefaultPollInterval;
        _portNameProvider = portNameProvider ?? SerialPort.GetPortNames;
    }

    /// <inheritdoc />
    public event EventHandler? DeviceRemoved;

    /// <inheritdoc />
    public void Start()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_timer != null)
            {
                return;
            }

            // Baseline before the first tick so the ports already present at startup are not
            // mistaken for arrivals — and, more importantly, so the very first tick can already
            // detect a removal.
            _knownPorts = TryGetPortNames();

            var generation = ++_generation;
            _timer = new Timer(_ => Poll(generation), null, _pollInterval, _pollInterval);
        }
    }

    /// <inheritdoc />
    public void Stop()
    {
        lock (_sync)
        {
            StopCore();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            StopCore();
        }
    }

    /// <summary>Tears down the current cycle. Caller holds <see cref="_sync"/>.</summary>
    private void StopCore()
    {
        // Bumping the generation retires every callback already in flight: Timer.Dispose does not
        // wait for one that is mid-enumeration, and blocking here for it could deadlock a Stop
        // called from the UI thread against a handler that dispatches to it.
        _generation++;
        _timer?.Dispose();
        _timer = null;
        _knownPorts = null;
    }

    private void Poll(int generation)
    {
        // A tick that arrives while the previous one is still sampling is dropped rather than
        // queued: the next tick re-reads the whole port table, so no removal can be missed.
        if (Interlocked.CompareExchange(ref _pollInProgress, 1, 0) != 0)
        {
            return;
        }

        try
        {
            var current = TryGetPortNames();
            if (current == null)
            {
                return;
            }

            bool anyRemoved;
            lock (_sync)
            {
                // Stopped, disposed, or restarted between this tick firing and now. The generation
                // check is what distinguishes "still the cycle I was created for" from "a later
                // Start() has already taken a fresh baseline"; without it a stale reading could be
                // compared against that new baseline and report a removal that never happened.
                if (_timer == null || _generation != generation)
                {
                    return;
                }

                anyRemoved = _knownPorts != null && !_knownPorts.IsSubsetOf(current);
                _knownPorts = current;
            }

            // Raised outside the lock so a handler can never re-enter this instance under it. The
            // generation is re-read first because Stop may have landed since: that cannot be an
            // airtight guarantee — Stop deliberately does not block on in-flight callbacks — but it
            // closes all of the window that this method itself controls. Consumers already have to
            // tolerate a silent backend (see IDeviceWatcher), and a late removal event is checked
            // against the live port table by the handler anyway.
            if (anyRemoved && Volatile.Read(ref _generation) == generation)
            {
                DeviceRemoved?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            // An exception escaping a timer callback terminates the process. Hotplug detection is
            // a convenience, so a failure here has to degrade to "no event" rather than crash a
            // running acquisition.
            _logger.Error(ex, "Serial port hotplug poll failed.");
        }
        finally
        {
            Interlocked.Exchange(ref _pollInProgress, 0);
        }
    }

    /// <summary>
    /// Reads the port table, returning null when it could not be read. A null result leaves the
    /// previous snapshot in place, so a transient enumeration failure is never misread as every
    /// device having been unplugged at once.
    /// </summary>
    private HashSet<string>? TryGetPortNames()
    {
        try
        {
            var names = new HashSet<string>(_portNameProvider(), StringComparer.OrdinalIgnoreCase);
            _enumerationFailureLogged = false;
            return names;
        }
        catch (Exception ex)
        {
            if (!_enumerationFailureLogged)
            {
                _enumerationFailureLogged = true;
                _logger.Warning(
                    ex, "Failed to enumerate serial ports while watching for device removal.");
            }

            return null;
        }
    }
}
