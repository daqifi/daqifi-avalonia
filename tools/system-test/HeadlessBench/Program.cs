using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Daqifi.Desktop;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Device;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.ViewModels;
using Optris.Icons.Avalonia;
using Optris.Icons.Avalonia.MaterialDesign;
using ChannelDirection = Daqifi.Core.Channel.ChannelDirection;

// HeadlessBench — the avalonia-full-test skill's T2/T3 rig. See
// ~/.claude/skills/avalonia-full-test/references/harness.md for the why; the short version:
//
//   * Boots the real app headless the way tools/parity-audit/AvaloniaCapture does.
//   * Connects through ConnectionDialogViewModel.ConnectManualSerialCommand — the user's path —
//     not by constructing a SerialStreamingDevice, so registration, duplicate check, status
//     string and hot-plug hand-off all run.
//   * Builds the Devices and Channels pane view models the way their views build them
//     (DevicesPanePrototype.axaml.cs / ChannelsPanePrototype.axaml.cs both just `new` one), so the
//     tile rows and the channel-drawer rows drive real commands and read real tiles.
//   * Every Step: drive → assert what Core/the device says → pump → assert what the UI shows →
//     capture a PNG → emit a results.jsonl line. Both assertions, always: a device that streams
//     while the graph stays flat is the lie this rig exists to catch.
//   * DAQIFI_TEST_MODE=1 swaps the firewall message box for a no-op and leaves the HID bootloader
//     watcher unstarted (it takes exclusive HID handles). It does not disable serial connection.
//   * DAQIFI_DATA_DIR is always set — under <out>/appdata — so a run can never touch the user's
//     real ~/Library/Application Support/DAQiFi.
//
// Usage:
//   HeadlessBench --port /dev/cu.usbmodemNNNN --out <run-dir> [--rate 100] [--seconds 5]
//   HeadlessBench --scripted <state> --out <run-dir>          (T1; states not yet implemented)
//
// Exit code 1 on any [FAIL]. Rows covered: CONN-USB, DEV-INFO, DEV-TILE, CH-TILE, DEV-RATE, CH-AI,
// STREAM-AI, LOG-SESSION, GRAPH-LIVE, CH-DIO, CH-PWM, SD-LIST, CONN-DISC. Add a Step per matrix
// row; keep the shape.

internal static class HeadlessBench
{
    private static string _out = "";
    private static string _port = "";
    private static int _rate = 100;
    private static int _seconds = 5;
    private static string? _scripted;
    private static bool _failed;
    private static readonly List<double> PumpLatenciesMs = [];
    private static readonly Vector Dpi = new(96, 96);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static int Main(string[] args)
    {
        if (!ParseArgs(args)) { return 2; }

        // Setup is inside the guard: an unwritable --out or a failed icon registration is an
        // argument problem, and exit 2 is what the usage block documents for one. Outside the
        // guard it was a stack trace instead, from a process that had not yet touched the board.
        try
        {
            Directory.CreateDirectory(Path.Combine(_out, "shots"));
            Directory.CreateDirectory(Path.Combine(_out, "appdata"));
            // Truncate rather than append. Two runs sharing an --out used to interleave their
            // verdicts in one file with nothing marking the boundary, so a reader could not tell
            // which run a [FAIL] came from; losing the older run is the lesser harm.
            File.WriteAllText(Path.Combine(_out, "results.jsonl"), "");
            Environment.SetEnvironmentVariable("DAQIFI_TEST_MODE", "1");
            Environment.SetEnvironmentVariable("DAQIFI_DATA_DIR", Path.Combine(_out, "appdata"));
            IconProvider.Current.Register<MaterialDesignIconProvider>();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"could not prepare --out '{_out}': {ex.Message}");
            return 2;
        }

        var lifetime = new ClassicDesktopStyleApplicationLifetime
        {
            Args = args,
            ShutdownMode = ShutdownMode.OnExplicitShutdown,
        };

        try
        {
            AppBuilder.Configure<Daqifi.Avalonia.App>()
                .UseSkia()
                .UseHarfBuzz()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
                .SetupWithLifetime(lifetime);
            Console.WriteLine("[OK]   app boot");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FAIL] app boot: {ex}");
            return 1;
        }

        try
        {
            var main = lifetime.MainWindow ?? throw new InvalidOperationException("no MainWindow after boot");
            main.Width = 1440; main.Height = 900;
            Pump();
            var shell = main.DataContext as DaqifiViewModel
                        ?? throw new InvalidOperationException($"MainWindow.DataContext is {main.DataContext?.GetType().Name ?? "null"}, expected DaqifiViewModel");

            if (_scripted is not null)
            {
                Emit(1, "SCRIPTED", "works", "not-run", $"--scripted {_scripted}: state not implemented in this stub");
                Console.WriteLine($"[INFO] scripted state '{_scripted}' not implemented yet");
            }
            else
            {
                RunHardwareSequence(main, shell);
            }
        }
        catch (Exception ex)
        {
            _failed = true;
            Console.WriteLine($"[FAIL] run: {ex}");
        }
        finally
        {
            try { lifetime.Shutdown(); Pump(); }
            catch (Exception ex) { Console.WriteLine($"[WARN] shutdown: {ex.Message}"); }
        }

        ReportPumpLatency();
        Console.WriteLine($"done -> {_out}");
        return _failed ? 1 : 0;
    }

    // ---------------------------------------------------------------- the T2 sequence

    private static void RunHardwareSequence(Window main, DaqifiViewModel shell)
    {
        try
        {
            RunHardwareSteps(main, shell);
        }
        finally
        {
            // The normal path stops logging and disconnects itself, so this is only for the paths
            // that never reach it. Without it, an exception after IsLogging=true leaves the board
            // streaming into a process that has already exited, and the NEXT run connects to a
            // device that is still pushing samples — on a shared bench board, somebody else's
            // mystery failure.
            StopAndDisconnect(shell);
        }
    }

    /// <summary>Best-effort undo of anything the sequence left running. Idempotent: on the normal
    /// path both halves are already done and this does nothing.</summary>
    private static void StopAndDisconnect(DaqifiViewModel shell)
    {
        try
        {
            if (shell.IsLogging)
            {
                Console.WriteLine("[WARN] cleanup: logging still on, stopping it");
                shell.IsLogging = false;
                PumpUntil(() => !LoggingManager.Instance.Active, TimeSpan.FromSeconds(5));
            }
        }
        catch (Exception ex) { Console.WriteLine($"[WARN] cleanup stop logging: {ex.Message}"); }

        foreach (var stranded in shell.ConnectedDevices.ToList())
        {
            try
            {
                Console.WriteLine($"[WARN] cleanup: disconnecting '{stranded.DeviceSerialNo}' left connected by an aborted run");
                shell.DisconnectDeviceCommand.Execute(stranded);
                PumpUntil(() => !shell.ConnectedDevices.Contains(stranded), TimeSpan.FromSeconds(10));
            }
            catch (Exception ex) { Console.WriteLine($"[WARN] cleanup disconnect: {ex.Message}"); }
        }
    }

    private static void RunHardwareSteps(Window main, DaqifiViewModel shell)
    {
        var threadsBefore = Process.GetCurrentProcess().Threads.Count;

        // CONN-USB — through the dialog's manual-port path, the same code a user's click runs.
        var dialog = new ConnectionDialogViewModel();
        dialog.ManualPortName = _port;
        var sw = Stopwatch.StartNew();
        var connectTask = dialog.ConnectManualSerialCommand.ExecuteAsync(null);
        PumpUntil(() => connectTask.IsCompleted && shell.ConnectedDevices.Count > 0, TimeSpan.FromSeconds(20));
        var device = shell.ConnectedDevices.FirstOrDefault();
        Step(2, "CONN-USB", "works", device is not null,
             device is not null
                 ? $"connected {_port} in {sw.Elapsed.TotalSeconds:F1} s; ConnectedDevices={shell.ConnectedDevices.Count}; status='{ConnectionManager.Instance.ConnectionStatusString}'"
                 : $"no device in ConnectedDevices after 20 s; status='{ConnectionManager.Instance.ConnectionStatusString}'",
             Capture(main, "t2-01-connected"), sw.Elapsed.TotalSeconds);
        if (device is null) { return; }

        // DEV-INFO — what the app knows about the board. The UI half of this row is the PNG.
        var serial = device.DeviceSerialNo;
        var fw = device.DeviceVersion;
        Step(2, "DEV-INFO", "works", !string.IsNullOrWhiteSpace(serial) && !string.IsNullOrWhiteSpace(fw),
             $"serial='{serial}' fw='{fw}' name='{device.DeviceDisplayName}' channels={device.DataChannels.Count}",
             Capture(main, "t2-02-devices"));

        // The two panes, built exactly as their views build them: DevicesPanePrototype.axaml.cs does
        // `new DevicesPaneViewModel(shell)` and ChannelsPanePrototype.axaml.cs does
        // `new ChannelsPaneViewModel()`, each in OnDataContextChanged. #260 assumed reaching these
        // needed a helper layer because the shell does not expose them; it does not — constructing
        // one IS the user's path, and both populate themselves from ConnectionManager. They stay
        // alive for the rest of the run, as they do in the app, so the tiles below are live.
        using var devicesPane = new DevicesPaneViewModel(shell);
        using var channelsPane = new ChannelsPaneViewModel();
        Pump();

        // DEV-TILE — one tile per connected device, and the drawer that opens on a tile click reads
        // the device's own rate back. FrequencyHz is a device read, so a tile bound to a stale or
        // duplicated device object shows the wrong board's rate.
        devicesPane.OpenSettingsCommand.Execute(devicesPane.Devices.FirstOrDefault());
        Pump();
        Step(2, "DEV-TILE", "works",
             devicesPane.Devices.Count == shell.ConnectedDevices.Count
                 && devicesPane.HasConnectedDevice
                 && devicesPane.Devices.Any(t => ReferenceEquals(t.Device, device))
                 && ReferenceEquals(devicesPane.SelectedDevice, device),
             $"{devicesPane.Devices.Count} tile(s) for {shell.ConnectedDevices.Count} connected device(s); " +
             $"HasConnectedDevice={devicesPane.HasConnectedDevice}; drawer open on '{devicesPane.SelectedDevice?.DeviceSerialNo}' " +
             $"showing FrequencyHz={devicesPane.FrequencyHz}",
             Capture(main, "t2-03-device-tile"));
        devicesPane.CloseSettingsCommand.Execute(null);
        Pump();

        // CH-TILE — every channel the device reports gets exactly one tile, shelved by kind. The
        // count is the check: a channel with no tile is unreachable in the UI, and the pane drops
        // analog OUTPUTS on the floor by design (an Nq1 reports none), so they are excluded here
        // rather than counted as missing.
        var expectedAnalogIn = device.DataChannels.Count(c => c.IsAnalog && c.Direction == ChannelDirection.Input);
        var expectedDigitalOut = device.DataChannels.Count(c => c.IsDigital && (c.Direction == ChannelDirection.Output || c.IsPwmEnabled));
        var expectedDigitalIn = device.DataChannels.Count(c => c.IsDigital) - expectedDigitalOut;
        Step(2, "CH-TILE", "works",
             channelsPane.AnalogInputs.Count == expectedAnalogIn
                 && channelsPane.DigitalInputs.Count == expectedDigitalIn
                 && channelsPane.DigitalOutputs.Count == expectedDigitalOut
                 && channelsPane.HasConnectedDevice,
             $"tiles AI={channelsPane.AnalogInputs.Count}/{expectedAnalogIn} " +
             $"DI={channelsPane.DigitalInputs.Count}/{expectedDigitalIn} " +
             $"DO={channelsPane.DigitalOutputs.Count}/{expectedDigitalOut} " +
             $"against {device.DataChannels.Count} channels on '{device.DeviceDisplayName}' " +
             $"({device.DataChannels.Count(c => c.IsAnalog && c.IsOutput)} analog outputs, which the pane does not tile)",
             Capture(main, "t2-04-channel-tiles"));

        // DEV-RATE — the drawer's FREQUENCY control binds DevicesPaneViewModel.FrequencyHz, which
        // writes DaqifiViewModel.SelectedStreamingFrequency; that setter is guarded (it refuses a
        // change mid-session) and it writes back the value the device settled on. Assigning
        // device.StreamingFrequency directly, as this rig used to before streaming, skips the guard
        // and leaves the shell's own value stale — the STREAM-AI rate below now comes from here.
        // Selecting the device first is what clicking its tile does.
        shell.SelectedDevice = device;
        Pump();
        shell.SelectedStreamingFrequency = _rate;
        Pump();
        Step(2, "DEV-RATE", "works", device.StreamingFrequency == _rate && shell.SelectedStreamingFrequency == _rate,
             $"asked for {_rate} Hz through SelectedStreamingFrequency; device.StreamingFrequency={device.StreamingFrequency}, shell.SelectedStreamingFrequency={shell.SelectedStreamingFrequency}",
             Capture(main, "t2-05-rate"));

        var ai = device.DataChannels.FirstOrDefault(c => !c.IsDigital && !c.IsOutput);
        if (ai is null)
        {
            Emit(2, "CH-AI", "works", "not-run", $"device reports no analog inputs ({device.DataChannels.Count} channels total)");
            Emit(2, "STREAM-AI", "works", "not-run", "no analog input to stream");
            Emit(2, "GRAPH-LIVE", "works", "not-run", "no analog input to stream");
            Emit(2, "LOG-SESSION", "works", "not-run", "no analog input to stream");
        }
        else
        {
            // CH-AI — clicking the channel's tile runs ChannelsPaneViewModel.ToggleChannelCommand,
            // which gives the device the channel AND subscribes it in the logging manager. Only the
            // second half feeds the live plot; AddChannel alone streams into nothing, which is how
            // the first run of this rig produced a "No channels streaming" graph while samples
            // arrived. This rig used to make both calls itself, hand-mirroring a private method —
            // now the pane exists, the command is reachable and the copy is gone.
            var aiTile = channelsPane.AnalogInputs.FirstOrDefault(t => ReferenceEquals(t.Channel, ai));
            channelsPane.ToggleChannelCommand.Execute(aiTile);
            Pump();
            var subscribed = LoggingManager.Instance.SubscribedChannels.Any(c => ReferenceEquals(c, ai));
            Step(2, "CH-AI", "works", ai.IsActive && subscribed && aiTile is { IsActive: true },
                 $"'{ai.Name}' IsActive={ai.IsActive} subscribed={subscribed}; tile IsActive={aiTile?.IsActive.ToString() ?? "no tile"}; " +
                 $"pane ActiveAnalogCount={channelsPane.ActiveAnalogCount}/{channelsPane.TotalAnalogCount}; " +
                 $"CanToggleLogging={shell.CanToggleLogging}",
                 Capture(main, "t2-06-channel-on"));

            // STREAM-AI + LOG-SESSION + GRAPH-LIVE — the user path is the LOGGING toggle: it checks
            // disk space, opens a session, and LoggingFleet.Start streams every connected device.
            // Count samples AND record when each one reached the app's channel object, plus the
            // device's own timestamp. A bare count over a window under-reads when delivery is batched
            // (a 1 s batch missing from a 5 s window looks like an 80 Hz device); the max arrival gap
            // and the device-clock span separate "the device sampled slower" from "the app got the
            // samples late", which are different findings against different layers.
            var samples = 0;
            DateTime? first = null;
            var arrivals = new List<long>(_rate * (_seconds + 2));
            long firstTicks = 0, lastTicks = 0;
            var arrivalSw = Stopwatch.StartNew();
            OnChannelUpdatedHandler counter = (_, sample) =>
            {
                first ??= DateTime.UtcNow;
                Interlocked.Increment(ref samples);
                lock (arrivals)
                {
                    arrivals.Add(arrivalSw.ElapsedMilliseconds);
                    if (firstTicks == 0) { firstTicks = sample.TimestampTicks; }
                    lastTicks = sample.TimestampTicks;
                }
            };
            sw.Restart();
            shell.IsLogging = true;
            // Subscribe AFTER the toggle, and the ordering is load-bearing. The setter assigns
            // LoggingManager.Active synchronously, and OnActiveChanged re-adds HandleChannelUpdate
            // to every subscribed channel — so a handler added here runs AFTER it in the multicast
            // delegate, and anything this counter has seen, the logger was already offered.
            // Subscribing before the toggle put this counter FIRST, which made `counted` unsound as
            // the persistence floor below: the stop can land between the two callbacks, so a sample
            // could increment `counted` and then be dropped by HandleChannelUpdate's own guard —
            // reading out as database loss that never happened. The reverse order is safe, because
            // a sample that arrives before this line is missed by `counted` and only makes the
            // floor lower.
            ai.OnChannelUpdated += counter;
            var started = PumpUntil(() => first is not null, TimeSpan.FromSeconds(10));
            var latency = sw.Elapsed.TotalSeconds;
            if (started) { PumpFor(TimeSpan.FromSeconds(_seconds)); }
            var window = first is null ? 0 : (DateTime.UtcNow - first.Value).TotalSeconds;
            var counted = samples;
            // Read the plot BEFORE stopping: LoggingManager clears the PlotLogger when Active goes false.
            // LoggedPoints, not LoggedChannels[..].Points: the LineSeries render through ItemsSource,
            // so their own Points lists stay empty while the plot is visibly drawing.
            var plotted = shell.Plotter?.LoggedPoints?.Values.Sum(l => l.Count) ?? -1;
            var plotSeries = shell.Plotter?.LoggedChannels?.Count ?? 0;
            // Hold the session BEFORE stopping — LoggingManager.Session is this run's row, and
            // LOG-SESSION below waits for its persisted sample count.
            var session = LoggingManager.Instance.Session;
            shell.IsLogging = false;
            PumpUntil(() => !LoggingManager.Instance.Active, TimeSpan.FromSeconds(5));
            ai.OnChannelUpdated -= counter;
            // Session finalization is a fire-and-forget Task.Run that counts the session's rows in
            // the database and marshals the result back onto the UI thread, so it needs the pump to
            // land. SampleCount stays null until that COUNT returns: it is the one signal in the app
            // that the samples actually reached disk, which is what LOG-SESSION claims to check.
            //
            // Require it to account for every sample seen before the stop, not merely to be
            // positive: PersistSessionSampleCount waits a bounded 10 s for the database writer to
            // drain and then stores whatever COUNT returns, so a half-written session persists a
            // positive undercount and would satisfy a > 0 test. The comparison is >= and not ==
            // deliberately — `counted` is snapshotted before IsLogging goes false, so every sample
            // in it was delivered while the session was active and must be on disk, while samples
            // arriving during the stop itself may also land. The boundary can only add rows, so
            // equality would be flaky in the direction that does not indicate a bug.
            var persisted = started && PumpUntil(() => session?.SampleCount >= counted, TimeSpan.FromSeconds(30));
            Pump();

            var shot = Capture(main, "t2-07-streamed");
            var effective = window > 0 ? counted / window : 0;
            var within = started && Math.Abs(effective - _rate) <= _rate * 0.1;
            Step(2, "STREAM-AI", "works", within,
                 started
                     ? $"{counted} samples in {window:F1} s after a {latency:F1} s start latency -> {effective:F1} Hz effective (set {_rate} Hz) on '{ai.Name}'; IsStreaming after stop={(device as AbstractStreamingDevice)?.IsStreaming}"
                     : $"no sample within 10 s of IsLogging=true at {_rate} Hz; LoggingManager.Active={LoggingManager.Instance.Active}",
                 shot, window);
            var listed = session is not null && LoggingManager.Instance.LoggingSessions.Any(s => s.ID == session.ID);
            Step(2, "LOG-SESSION", "works",
                 started && !LoggingManager.Instance.Active && !shell.IsLogging && persisted && listed,
                 session is null
                     ? $"no LoggingSession was created; SessionStartFailure='{LoggingManager.Instance.SessionStartFailure}'"
                     : $"IsLogging toggled on/off; LoggingManager.Active={LoggingManager.Instance.Active}; IsLogging={shell.IsLogging}; " +
                       $"session {session.ID} '{session.Name}' persisted SampleCount=" +
                       $"{session.SampleCount?.ToString(CultureInfo.InvariantCulture) ?? "null (finalization did not report within 30 s)"} " +
                       $"counted in the database, against {counted} samples seen before the stop — " +
                       $"{(session.SampleCount is { } n ? (n >= counted ? $"all accounted for (+{n - counted} across the stop boundary)" : $"SHORT BY {counted - n}") : "not comparable")}; " +
                       $"listed in LoggingSessions={listed}",
                 shot);
            Step(2, "GRAPH-LIVE", "works", plotted > 0,
                 plotted >= 0
                     ? $"PlotLogger had {plotted} points across {plotSeries} series while streaming (read before stop clears it)"
                     : "shell.Plotter is null — live plot not constructed",
                 shot);

            // Delivery shape — the "unexpected" half of STREAM-AI.
            long maxGap = 0, gapsOver100 = 0;
            lock (arrivals)
            {
                for (var i = 1; i < arrivals.Count; i++)
                {
                    var g = arrivals[i] - arrivals[i - 1];
                    if (g > maxGap) { maxGap = g; }
                    if (g > 100) { gapsOver100++; }
                }
            }
            // DataSample.TimestampTicks are DateTime ticks (100 ns) — Core has already converted the
            // device's counter (TimestampFrequency, 42 MHz on an Nq1) into a DateTime. So the span
            // below is "how much time the device *claims* passed"; the wall-clock window above is how
            // much actually did. When the two disagree the finding is about the device's timebase.
            var tsFreq = (device as AbstractStreamingDevice)?.TimestampFrequency ?? 0;
            var deviceSpan = lastTicks > firstTicks ? (lastTicks - firstTicks) / (double)TimeSpan.TicksPerSecond : double.NaN;
            var deviceRate = double.IsNaN(deviceSpan) || deviceSpan <= 0 ? double.NaN : (counted - 1) / deviceSpan;
            var clockSkew = double.IsNaN(deviceSpan) || window <= 0 ? double.NaN : deviceSpan / window;
            Emit(2, "STREAM-AI", "unexpected", maxGap > 250 || Math.Abs(clockSkew - 1) > 0.05 ? "finding" : "pass",
                 $"arrival gaps: max {maxGap} ms, {gapsOver100} over 100 ms across {arrivals.Count} samples; " +
                 $"device-stamped span {deviceSpan:F2} s vs wall-clock {window:F2} s (ratio {clockSkew:F3}; device counter {tsFreq} Hz) " +
                 $"-> {deviceRate:F1} Hz by device timestamps, {(window > 0 ? counted / window : 0):F1} Hz by wall clock");
        }

        // CH-DIO — the drawer's DIRECTION and OUTPUT STATE radios bind SelectedChannel.IsOutput and
        // SelectedChannel.IsDigitalOn (ChannelsPanePrototype.axaml), and the drawer opens on the
        // tile's gear — ChannelsPaneViewModel.OpenSettingsCommand. Calling device.SetChannelDirection
        // / device.SetChannelOutputValue instead sends the SCPI write WITHOUT moving the bound
        // property, so the tile keeps showing the old state: the second trap in this rig's README,
        // and the one that produced two false failures the last time these rows were written.
        // A PWM-capable line is preferred so CH-PWM below can use the same channel.
        var dio = device.DataChannels.FirstOrDefault(c => c.IsDigital && c.IsPwmCapable)
                  ?? device.DataChannels.FirstOrDefault(c => c.IsDigital);
        if (dio is null)
        {
            Emit(2, "CH-DIO", "works", "not-run", $"device reports no digital channels ({device.DataChannels.Count} channels total)");
            Emit(2, "CH-PWM", "works", "not-run", "no digital channel to drive");
        }
        else
        {
            // Everything this pair touches — direction, drive state, PWM mode, duty — is state the
            // BOARD keeps across a host disconnect. So snapshot all four, not just the two these
            // rows write directly, and restore from a `finally`: an exception between the PWM
            // enable and disable below would otherwise leave a shared bench board driving a pin
            // nobody asked it to drive, which is the same harm the run-level StopAndDisconnect
            // exists to prevent for streaming.
            var dioWasOutput = dio.IsOutput;
            var dioWasOn = dio.IsDigitalOn;
            var dioWasPwm = dio.IsPwmEnabled;
            var dioWasDuty = dio.PwmDutyCyclePercent;
            try
            {
                if (dio.IsPwmEnabled)
                {
                    // Same persistence, read the other way: this row can be HANDED a pin already in
                    // PWM by a previous run or another user of the bench. While PWM is on the tile
                    // renders "PWM n%" and hides the drive toggle, so CH-DIO's assertions below
                    // would fail on inherited state rather than on anything this run did. Establish
                    // the state the row is about; the finally puts PWM back.
                    Console.WriteLine($"[WARN] '{dio.Name}' arrived with PWM enabled; disabling it for CH-DIO");
                    dio.IsPwmEnabled = false;
                    Pump();
                }

                channelsPane.OpenSettingsCommand.Execute(FindTile(channelsPane, dio));
                Pump();
                var opened = channelsPane.IsSettingsOpen && ReferenceEquals(channelsPane.SelectedChannel, dio);
                dio.IsOutput = true;
                // Flipping direction re-shelves the tile, and the pane posts that rebuild at
                // Background priority — so the tile object is disposed and replaced on a later pump,
                // and every read below re-fetches rather than holding the one from before the flip.
                PumpUntil(() => channelsPane.DigitalOutputs.Any(t => ReferenceEquals(t.Channel, dio)), TimeSpan.FromSeconds(5));
                dio.IsDigitalOn = true;
                Pump();
                var dioTile = FindTile(channelsPane, dio);
                Step(2, "CH-DIO", "works",
                     opened && dio.IsOutput && dio.Direction == ChannelDirection.Output && dio.IsDigitalOn
                         && dioTile is { Value: "HIGH", TypeLabel: "DIGITAL OUT", ShowDriveToggle: true }
                         && channelsPane.DigitalOutputs.Contains(dioTile),
                     $"'{dio.Name}': drawer open on it={opened}; IsOutput={dio.IsOutput} Direction={dio.Direction} " +
                     $"IsDigitalOn={dio.IsDigitalOn}; tile label='{dioTile?.TypeLabel}' value='{dioTile?.Value}', " +
                     $"shelved in {(dioTile is not null && channelsPane.DigitalOutputs.Contains(dioTile) ? "DigitalOutputs" : "the wrong section")}",
                     Capture(main, "t2-08-digital-out"));

                if (!dio.IsPwmCapable)
                {
                    Emit(2, "CH-PWM", "works", "not-run",
                         $"no PWM-capable digital channel on this board ('{dio.Name}' is not one; " +
                         $"{device.DataChannels.Count(c => c.IsDigital)} digital channels)");
                }
                else
                {
                    // CH-PWM — the drawer's PWM toggle and DUTY CYCLE slider bind
                    // SelectedChannel.IsPwmEnabled and .PwmDutyCyclePercent. IsPwmEnabled reads
                    // Core's mirror of the last state it actually commanded, so a command the device
                    // refuses leaves it false: that is the assertion doing the work here, not the
                    // write. Two different write paths, so both are checked: with PWM off the setter
                    // only updates Core's bookkeeping and the value is applied by the next enable;
                    // with PWM on it is commanded live.
                    dio.PwmDutyCyclePercent = 25;
                    var seeded = dio.PwmDutyCyclePercent;
                    dio.IsPwmEnabled = true;
                    Pump();
                    var pwmOn = dio.IsPwmEnabled;
                    dio.PwmDutyCyclePercent = 60;
                    PumpUntil(() => channelsPane.DigitalOutputs.Any(t => ReferenceEquals(t.Channel, dio)), TimeSpan.FromSeconds(5));
                    var pwmTile = FindTile(channelsPane, dio);
                    Step(2, "CH-PWM", "works",
                         pwmOn && seeded == 25 && dio.PwmDutyCyclePercent == 60
                             && pwmTile is { IsPwmActive: true, ShowDriveToggle: false, Value: "PWM 60%" },
                         $"'{dio.Name}' seeded {seeded}% before enabling, IsPwmEnabled={pwmOn}, then " +
                         $"duty={dio.PwmDutyCyclePercent}% commanded live at the device-wide " +
                         $"{device.PwmFrequencyHz} Hz; tile value='{pwmTile?.Value}' IsPwmActive={pwmTile?.IsPwmActive} " +
                         $"ShowDriveToggle={pwmTile?.ShowDriveToggle} (the drive toggle is hidden while PWM runs — " +
                         "the hardware ignores digital state writes on a PWM-active channel)",
                         Capture(main, "t2-09-pwm"));

                    // Limits. The slider is 1-100, but the property is what a binding writes, and
                    // Core rejects a duty of 0 outright (the firmware stores it and never applies
                    // it, so the old duty keeps toggling). Both ends must coerce rather than command.
                    dio.PwmDutyCyclePercent = 0;
                    var coercedLow = dio.PwmDutyCyclePercent;
                    dio.PwmDutyCyclePercent = 101;
                    var coercedHigh = dio.PwmDutyCyclePercent;
                    Step(2, "CH-PWM", "limits", coercedLow == 1 && coercedHigh == 100,
                         $"duty 0 -> {coercedLow}%, duty 101 -> {coercedHigh}% (commandable range is 1-100)", null);

                    dio.IsPwmEnabled = false;
                    Pump();
                    // Disabling PWM leaves the pin transiently high-impedance and Core zeroes the
                    // channel's stored output value; the app hydrates IsDigitalOn from that, so the
                    // tile cannot go on claiming HIGH for a pin nothing is driving.
                    var settledTile = FindTile(channelsPane, dio);
                    Emit(2, "CH-PWM", "unexpected", !dio.IsPwmEnabled && !dio.IsDigitalOn ? "pass" : "finding",
                         $"after disabling PWM: IsPwmEnabled={dio.IsPwmEnabled} IsDigitalOn={dio.IsDigitalOn}, " +
                         $"tile value='{settledTile?.Value}'");
                }
            }
            finally
            {
                // Restore in an order the hardware accepts: PWM off first, because the firmware
                // ignores direction and state writes while it drives the pin; then the duty, which
                // with PWM off is bookkeeping only; then the drive state and the direction, so the
                // pin is taken low before it is released back to an input; and PWM last, since
                // re-enabling it re-commands the duty just restored in Core's documented
                // duty → frequency → enable order. Best-effort, and it must not mask a real
                // failure above — hence its own guard, like StopAndDisconnect's.
                try
                {
                    dio.IsPwmEnabled = false;
                    dio.PwmDutyCyclePercent = dioWasDuty;
                    dio.IsDigitalOn = dioWasOn;
                    dio.IsOutput = dioWasOutput;
                    dio.IsPwmEnabled = dioWasPwm;
                    Pump();
                }
                catch (Exception ex) { Console.WriteLine($"[WARN] cleanup restore '{dio.Name}': {ex.Message}"); }
                channelsPane.CloseSettingsCommand.Execute(null);
                Pump();
            }
        }

        // SD-LIST — the Logged Data pane's device-file list. shell.DeviceLogsViewModel is the
        // instance that pane binds (LoggedDataPanePrototype.axaml) and RefreshFilesCommand is its
        // REFRESH button. It runs here, after the stream has stopped, because the app refuses SD
        // file access while the device is streaming or logging to its card
        // (SdOperationBlockedException -> SdCardState.Busy). Selecting a device fires a refresh of
        // its own, so wait for the command to come free rather than racing it: a second
        // ExecuteAsync while the first is in flight is dropped, and this row would then be
        // asserting on the earlier listing without saying so.
        var logs = shell.DeviceLogsViewModel;
        logs.SelectedDevice = device;
        PumpUntil(() => !logs.IsBusy && logs.RefreshFilesCommand.CanExecute(null), TimeSpan.FromSeconds(60));
        sw.Restart();
        var listing = logs.RefreshFilesCommand.ExecuteAsync(null);
        PumpUntil(() => listing.IsCompleted, TimeSpan.FromSeconds(60));
        Pump();
        var sdShot = Capture(main, "t2-10-sd-files");
        if (logs.SdCardState == SdCardState.NotPresent)
        {
            Emit(2, "SD-LIST", "works", "not-run",
                 $"device reports no SD card installed; status line '{logs.SdCardStatusLine}'", sdShot);
        }
        else
        {
            Step(2, "SD-LIST", "works",
                 logs.SdCardState == SdCardState.Ok && logs.DeviceFiles.Count == device.SdCardFiles.Count,
                 $"listed {logs.DeviceFiles.Count} file(s) in {sw.Elapsed.TotalSeconds:F1} s against the " +
                 $"{device.SdCardFiles.Count} the device layer holds; SdCardState={logs.SdCardState}; " +
                 $"HasFiles={logs.HasFiles} HasNoFiles={logs.HasNoFiles}; status line '{logs.SdCardStatusLine}'" +
                 (logs.SdCardState == SdCardState.Ok ? "" : $"; error='{logs.SdCardErrorMessage}'"),
                 sdShot, sw.Elapsed.TotalSeconds);
        }

        // CONN-DISC — through DaqifiViewModel.DisconnectDeviceCommand, the command the Devices pane
        // binds (DevicesPanePrototype.axaml, DevicesMobileView.axaml, and DevicesPaneViewModel.
        // DisconnectSelected). ConnectionManager.Instance.Disconnect is only ONE of the four things
        // that command does — it also unsubscribes every active channel from LoggingManager, removes
        // the firmware notification and clears SelectedDevice — and this rig used to call the
        // manager directly, so those three went untested. Note which assertion below actually
        // discriminates: SelectedDevice. The unsubscribe reads clean either way, because
        // DaqifiViewModel's ConnectedDevices handler sweeps orphaned subscriptions for the
        // auto-removal paths and catches this one in passing (measured on the bench, #260).
        // Calling device.Disconnect() is worse than either: it closes the port but leaves the
        // device registered in the UI, exactly the stale-"Connected" state this row exists to catch.
        sw.Restart();
        shell.DisconnectDeviceCommand.Execute(device);
        PumpUntil(() => shell.ConnectedDevices.Count == 0 && ConnectionManager.Instance.ConnectedDevices.Count == 0, TimeSpan.FromSeconds(10));
        Pump();
        var threadsAfter = Process.GetCurrentProcess().Threads.Count;
        var leftSubscribed = LoggingManager.Instance.SubscribedChannels.Count;
        var selectionCleared = shell.SelectedDevice is null;
        Step(2, "CONN-DISC", "works",
             shell.ConnectedDevices.Count == 0 && ConnectionManager.Instance.ConnectedDevices.Count == 0
                 && leftSubscribed == 0 && selectionCleared,
             $"shell.ConnectedDevices={shell.ConnectedDevices.Count}; manager.ConnectedDevices={ConnectionManager.Instance.ConnectedDevices.Count}; " +
             $"SubscribedChannels left={leftSubscribed}; SelectedDevice cleared={selectionCleared}; " +
             $"status='{ConnectionManager.Instance.ConnectionStatusString}' (a per-connect status, not per-device — see #212)",
             Capture(main, "t2-11-disconnected"), sw.Elapsed.TotalSeconds);
        Emit(2, "CONN-DISC", "unexpected", threadsAfter > threadsBefore + 2 ? "finding" : "pass",
             $"threads before connect={threadsBefore}, after disconnect={threadsAfter}");
        Emit(2, "CONN-DISC", "unexpected", "not-run",
             "port-free check runs outside the process: `lsof /dev/cu.usbmodem*` after exit");
    }

    // ---------------------------------------------------------------- plumbing

    /// <summary>The pane's live tile for a channel, from whichever section it is shelved in. Always
    /// re-fetch after a change that can re-shelve it: the pane disposes and rebuilds every tile.</summary>
    private static ChannelTileViewModel? FindTile(ChannelsPaneViewModel pane, IChannel channel) =>
        pane.AnalogInputs.Concat(pane.DigitalInputs).Concat(pane.DigitalOutputs)
            .FirstOrDefault(t => ReferenceEquals(t.Channel, channel));

    private static void Step(int tier, string row, string check, bool pass, string evidence, string? artifact, double? seconds = null)
    {
        Emit(tier, row, check, pass ? "pass" : "fail", evidence, artifact, seconds);
        if (!pass) { _failed = true; }
        Console.WriteLine($"[{(pass ? "OK]  " : "FAIL]")} {row}/{check}: {evidence}");
    }

    private static void Emit(int tier, string row, string check, string status, string evidence, string? artifact = null, double? seconds = null)
    {
        var rec = new Dictionary<string, object?>
        {
            ["tier"] = tier, ["row"] = row, ["check"] = check, ["status"] = status, ["evidence"] = evidence,
        };
        if (artifact is not null) { rec["artifact"] = artifact; }
        if (seconds is not null) { rec["seconds"] = Math.Round(seconds.Value, 2); }
        File.AppendAllText(Path.Combine(_out, "results.jsonl"), JsonSerializer.Serialize(rec, Json) + "\n");
    }

    /// <summary>Drain the dispatcher and tick the headless render timer. Also the UI-stall probe:
    /// a single RunJobs that takes long is exactly what a user experiences as a freeze.</summary>
    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            var sw = Stopwatch.StartNew();
            Dispatcher.UIThread.RunJobs();
            PumpLatenciesMs.Add(sw.Elapsed.TotalMilliseconds);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void PumpFor(TimeSpan duration)
    {
        var deadline = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(10); }
    }

    private static bool PumpUntil(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            Pump();
            if (condition()) { return true; }
            Thread.Sleep(20);
        }
        return condition();
    }

    private static void ReportPumpLatency()
    {
        if (PumpLatenciesMs.Count == 0) { return; }
        var max = PumpLatenciesMs.Max();
        var over100 = PumpLatenciesMs.Count(l => l > 100);
        Emit(2, "GRAPH-LIVE", "unexpected", max > 250 ? "finding" : "pass",
             $"UI pump max {max:F0} ms; {over100} of {PumpLatenciesMs.Count} pumps over 100 ms");
    }

    /// <summary>Render the window to an opaque PNG, as AvaloniaCapture.Encode does. Returns the
    /// run-relative path for results.jsonl, or null (and a [WARN]) if rendering failed — a missing
    /// screenshot is worth knowing about but is not itself the test.</summary>
    private static string? Capture(Window w, string name)
    {
        try
        {
            Pump();
            var size = new PixelSize((int)Math.Max(1, w.Bounds.Width), (int)Math.Max(1, w.Bounds.Height));
            using var rendered = new RenderTargetBitmap(size, Dpi);
            rendered.Render(w);
            using var opaque = new WriteableBitmap(size, Dpi, PixelFormat.Rgba8888, AlphaFormat.Opaque);
            using (var locked = opaque.Lock())
            {
                rendered.CopyPixels(new PixelRect(size), locked.Address, locked.RowBytes * locked.Size.Height, locked.RowBytes);
            }
            var rel = Path.Combine("shots", name + ".png");
            using var fs = File.Create(Path.Combine(_out, rel));
            opaque.Save(fs);
            return rel;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[WARN] capture {name}: {ex.Message}");
            return null;
        }
    }

    private static bool ParseArgs(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            try
            {
                switch (args[i])
                {
                    case "--port": _port = Next(); break;
                    case "--out": _out = Path.GetFullPath(Next()); break;
                    case "--rate": _rate = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--seconds": _seconds = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                    case "--scripted": _scripted = Next(); break;
                    default: Console.WriteLine($"unknown argument '{args[i]}'"); return false;
                }
            }
            catch (Exception ex) { Console.WriteLine(ex.Message); return false; }
        }
        if (string.IsNullOrEmpty(_out)) { Console.WriteLine("--out <run-dir> is required"); return false; }
        if (_scripted is null && string.IsNullOrEmpty(_port)) { Console.WriteLine("--port <serial port> or --scripted <state> is required"); return false; }
        if (_rate < 1 || _seconds < 1) { Console.WriteLine("--rate and --seconds must be >= 1"); return false; }
        return true;
    }
}
