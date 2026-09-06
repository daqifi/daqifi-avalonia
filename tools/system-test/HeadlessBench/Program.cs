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

// HeadlessBench — the avalonia-full-test skill's T2/T3 rig. See
// ~/.claude/skills/avalonia-full-test/references/harness.md for the why; the short version:
//
//   * Boots the real app headless the way tools/parity-audit/AvaloniaCapture does.
//   * Connects through ConnectionDialogViewModel.ConnectManualSerialCommand — the user's path —
//     not by constructing a SerialStreamingDevice, so registration, duplicate check, status
//     string and hot-plug hand-off all run.
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
// Exit code 1 on any [FAIL]. Rows covered by this stub: CONN-USB, DEV-INFO, DEV-RATE, CH-AI,
// STREAM-AI, LOG-SESSION, GRAPH-LIVE, CONN-DISC. Add a Step per matrix row; keep the shape.

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
             Capture(main, "t2-03-rate"));

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
            // CH-AI — mirrors ChannelsPaneViewModel.ToggleChannel (private): the device gets the
            // channel AND the logging manager subscribes it. Only the second half feeds the live plot;
            // calling AddChannel alone streams into nothing, which is how the first run of this rig
            // produced a "No channels streaming" graph while samples arrived.
            device.AddChannel(ai);
            LoggingManager.Instance.Subscribe(ai);
            Pump();
            var subscribed = LoggingManager.Instance.SubscribedChannels.Any(c => ReferenceEquals(c, ai));
            Step(2, "CH-AI", "works", ai.IsActive && subscribed,
                 $"'{ai.Name}' IsActive={ai.IsActive} subscribed={subscribed}; CanToggleLogging={shell.CanToggleLogging}",
                 Capture(main, "t2-04-channel-on"));

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
            ai.OnChannelUpdated += counter;
            sw.Restart();
            shell.IsLogging = true;
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
            var persisted = PumpUntil(() => session?.SampleCount > 0, TimeSpan.FromSeconds(30));
            Pump();

            var shot = Capture(main, "t2-05-streamed");
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
                       $"session {session.ID} '{session.Name}' persisted SampleCount={session.SampleCount?.ToString(CultureInfo.InvariantCulture) ?? "null"} " +
                       $"(counted in the database) against {counted} samples seen; listed in LoggingSessions={listed}",
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

        // CONN-DISC — through DaqifiViewModel.DisconnectDeviceCommand, the command the Devices pane
        // binds (DevicesPanePrototype.axaml, DevicesMobileView.axaml, and DevicesPaneViewModel.
        // DisconnectSelected). ConnectionManager.Instance.Disconnect is only ONE of the four things
        // that command does — it also unsubscribes every active channel from LoggingManager, removes
        // the firmware notification and clears SelectedDevice — so calling the manager directly, as
        // this rig used to, passes while the other three are broken. Calling device.Disconnect() is
        // worse still: it closes the port but leaves the device registered in the UI, exactly the
        // stale-"Connected" state this row exists to catch.
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
             Capture(main, "t2-06-disconnected"), sw.Elapsed.TotalSeconds);
        Emit(2, "CONN-DISC", "unexpected", threadsAfter > threadsBefore + 2 ? "finding" : "pass",
             $"threads before connect={threadsBefore}, after disconnect={threadsAfter}");
        Emit(2, "CONN-DISC", "unexpected", "not-run",
             "port-free check runs outside the process: `lsof /dev/cu.usbmodem*` after exit");
    }

    // ---------------------------------------------------------------- plumbing

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
