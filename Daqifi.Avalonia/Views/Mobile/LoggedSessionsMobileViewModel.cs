using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Exporter;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.ViewModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OxyPlot;

namespace Daqifi.Avalonia.Views.Mobile;

/// <summary>
/// Mobile "APP LOGS" half of the Storage tab — the phone projection of the desktop
/// Logged Data pane's locally-logged session list (#7). Reads the same
/// <see cref="LoggingManager.Instance"/> session collection the desktop pane binds
/// to (populated on mobile by <c>App.InitializeMobile</c>) and offers the
/// non-destructive **export** action via the reused
/// <see cref="OptimizedLoggingSessionExporter"/> + the platform save picker.
///
/// The inline session PLOT VIEWER and DELETE / DELETE-ALL (#7 phase-2) reuse the desktop's
/// WPF-free building blocks directly — <see cref="SessionDataRepository"/> (load + transactional
/// delete over the injected DbContext factory) and <see cref="PlotModelFactory"/> for a styled
/// OxyPlot model, plus the reusable <see cref="ConfirmOverlayViewModel"/> for destructive confirms
/// (the desktop MahApps MessageBoxService is main-window-bound and NOT reusable on mobile). The
/// heavy DB read + plot build run off the UI thread; the viewer uses the same two-phase strategy
/// as the desktop (fast initial batch, full-range downsample for large sessions) so the phone never
/// materializes millions of rows.
/// </summary>
public partial class LoggedSessionsMobileViewModel : ObservableObject
{
    private readonly IAppLogger _appLogger = AppLogger.Instance;

    /// <summary>Set by the view (which owns the TopLevel): resolves a save FILE path
    /// for a single-session export given a suggested filename, or null if cancelled.</summary>
    public Func<string, Task<string?>>? SavePathResolver { get; set; }

    /// <summary>Set by the view: resolves a destination FOLDER for a multi-session
    /// "Export all" (one CSV per session), or null if cancelled.</summary>
    public Func<Task<string?>>? SaveFolderResolver { get; set; }

    public ObservableCollection<LoggingSession> Sessions { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSessions))]
    [NotifyPropertyChangedFor(nameof(HasNoSessions))]
    private bool _loaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoSessions))]
    [NotifyPropertyChangedFor(nameof(LoadFailed))]
    private bool _loadFailedFlag;

    public bool HasSessions => Sessions.Count > 0;

    // Only the definitive "no sessions" empty state — NOT a failed load, which would
    // otherwise render as "NO LOGGED SESSIONS" and hide that the list couldn't load.
    public bool HasNoSessions => Loaded && !LoadFailedFlag && Sessions.Count == 0;

    /// <summary>True when the last load threw — the view shows an error + Retry instead
    /// of the definitive empty state.</summary>
    public bool LoadFailed => LoadFailedFlag;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

    // --- Session viewer + delete (#7 phase-2) ---

    /// <summary>Nested confirm overlay for destructive delete actions. Reuses the WPF-free
    /// <see cref="ConfirmOverlayViewModel"/> the desktop panes use; the view binds to
    /// <c>ConfirmOverlay.*</c> and the awaited <see cref="ConfirmOverlayViewModel.ShowAsync"/>
    /// resolves when the user taps DELETE or cancels.</summary>
    public ConfirmOverlayViewModel ConfirmOverlay { get; } = new();

    /// <summary>Per-channel legend chips (name + colour swatch) for the currently-viewed session,
    /// mirroring the desktop's custom legend (the plot's own legend stays disabled).</summary>
    public ObservableCollection<ViewerChannel> ViewerChannels { get; } = new();

    /// <summary>The OxyPlot model for the viewed session, or null while loading / when it has no data.</summary>
    [ObservableProperty]
    private PlotModel? _sessionPlotModel;

    /// <summary>True while the session-viewer overlay is open.</summary>
    [ObservableProperty]
    private bool _isViewerOpen;

    /// <summary>True while the viewer is loading the session's samples off the UI thread.</summary>
    [ObservableProperty]
    private bool _isViewerLoading;

    /// <summary>The viewed session's name, shown in the viewer header.</summary>
    [ObservableProperty]
    private string _viewerTitle = string.Empty;

    /// <summary>Viewer status line (loading / empty / error); empty once the plot is shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewerHasStatus))]
    private string _viewerStatus = string.Empty;

    public bool ViewerHasStatus => !string.IsNullOrEmpty(ViewerStatus);

    /// <summary>Lazily-built read/delete repository over the mobile DI DbContext factory.</summary>
    private SessionDataRepository? _repository;

    /// <summary>The DbContext factory the repository was built over, cached for the static
    /// single-timestamp value-spread helper (which takes the factory directly).</summary>
    private IDbContextFactory<LoggingContext>? _factory;

    public LoggedSessionsMobileViewModel()
    {
        // Fire-and-forget: Reload hydrates off the UI thread and handles its own
        // errors, so there is nothing to await at construction.
        _ = Reload();
    }

    [RelayCommand]
    private async Task Reload()
    {
        if (IsBusy) { return; }
        IsBusy = true;
        try
        {
            // Hydrate straight from the DB, OFF the UI thread. Use the PURE-READ snapshot
            // (not LoadPersistedLoggingSessions): this pane re-reads on every Storage visit,
            // and LoadPersistedLoggingSessions PURGES empty-session rows as a side effect —
            // which would race the SD-card importer and delete its in-flight (0-sample)
            // session. GetLoggingSessionsSnapshot only SELECTs (the in-flight row is excluded
            // by the Samples.Any filter until it has data), so a refresh can't destroy it.
            // It returns a fresh list without touching the shared singleton collection, and
            // the EF Core query runs on a background thread so opening Storage never blocks.
            var loaded = await Task.Run(() => LoggingManager.Instance.GetLoggingSessionsSnapshot());
            // Marshal the ObservableCollection mutation onto the UI thread explicitly:
            // the await above does not guarantee resuming on the UI thread (e.g. the
            // fire-and-forget ctor path may have no captured context), and Sessions is
            // bound to the UI.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Sessions.Clear();
                foreach (var session in loaded)
                {
                    Sessions.Add(session);
                }
            });
            LoadFailedFlag = false;
            // Clear any prior error unconditionally on success — otherwise a successful
            // retry against an EMPTY db would show the definitive "NO LOGGED SESSIONS"
            // empty state AND the stale "Couldn't load…" error at the same time.
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            // Surface the failure: an empty Sessions list here means the load FAILED, not
            // that there are genuinely no sessions — so flag it and show an error + Retry
            // instead of the definitive "NO LOGGED SESSIONS" empty state.
            _appLogger.Error(ex, "Mobile: failed to load logged sessions");
            LoadFailedFlag = true;
            StatusMessage = "Couldn't load logged sessions. Tap Refresh to retry.";
        }
        finally
        {
            Loaded = true;
            IsBusy = false;
            OnPropertyChanged(nameof(HasSessions));
            OnPropertyChanged(nameof(HasNoSessions));
        }
    }

    [RelayCommand]
    private Task ExportSession(LoggingSession? session) => ExportSessionsAsync(session);

    [RelayCommand]
    private Task ExportAll() => ExportSessionsAsync(null);

    /// <summary>Exports a single session to a user-picked CSV file, or every session to a
    /// user-picked folder (one <c>{session}.csv</c> per session). Non-destructive; failures
    /// are logged and surfaced on the status line, never thrown.</summary>
    private async Task ExportSessionsAsync(LoggingSession? single)
    {
        // Guard re-entrancy: no overlapping export/reload, and no second picker while one
        // is open. IsBusy is held for the WHOLE operation (picker + write).
        if (IsBusy) { return; }
        if (single is not null ? SavePathResolver is null : SaveFolderResolver is null) { return; }

        var toExport = single is not null ? new[] { single } : Sessions.ToArray();
        if (toExport.Length == 0) { return; }

        IsBusy = true;
        try
        {
            StatusMessage = "Exporting…";
            if (single is not null)
            {
                // Single session → one picked file.
                string? path;
                try
                {
                    path = await SavePathResolver!($"{SafeName(single)}.csv");
                }
                catch (Exception ex)
                {
                    _appLogger.Error(ex, "Mobile: export save-picker failed");
                    StatusMessage = "Unable to open the export picker.";
                    return;
                }
                if (string.IsNullOrEmpty(path)) { StatusMessage = string.Empty; return; }

                // One exporter for the operation captures the CSV delimiter ONCE (its ctor
                // reads DaqifiSettings.CsvDelimiter), so toggling the delimiter in Settings
                // mid-export can't change it under us.
                var exporter = new OptimizedLoggingSessionExporter();
                var wrote = await Task.Run(() => ExportOne(exporter, single, path!, sessionIndex: 0, totalSessions: 1));
                // A listed session always has samples, so a false result here means the export
                // FAILED (locked/unwritable destination, disk full) — not that it was "empty".
                StatusMessage = wrote ? "Exported 1 session." : "Couldn't export that session — see logs.";
            }
            else
            {
                // Export all → one CSV per session into a picked folder. Each session must
                // get its OWN file: the exporter opens its StreamWriter with append=false,
                // so writing every session to a single path would leave only the last one.
                string? folder;
                try
                {
                    folder = await SaveFolderResolver!();
                }
                catch (Exception ex)
                {
                    _appLogger.Error(ex, "Mobile: export folder-picker failed");
                    StatusMessage = "Unable to open the folder picker.";
                    return;
                }
                if (string.IsNullOrEmpty(folder)) { StatusMessage = string.Empty; return; }

                // One exporter for the WHOLE batch captures the CSV delimiter once (its ctor
                // reads DaqifiSettings.CsvDelimiter), so toggling the delimiter in Settings
                // mid-"Export all" can't produce a folder of mixed-delimiter CSVs.
                var exporter = new OptimizedLoggingSessionExporter();
                var written = await Task.Run(() =>
                {
                    var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var count = 0;
                    for (var i = 0; i < toExport.Length; i++)
                    {
                        // Disambiguate duplicate / sanitization-colliding names so two sessions
                        // never target the same file (the exporter truncates, losing the earlier).
                        var name = UniqueFileName(SafeName(toExport[i]), usedNames);
                        var filepath = Path.Combine(folder!, $"{name}.csv");
                        if (ExportOne(exporter, toExport[i], filepath, sessionIndex: i, totalSessions: toExport.Length)) { count++; }
                    }
                    return count;
                });
                StatusMessage = written == 0
                    ? "Nothing was exported."
                    : written == toExport.Length
                        ? $"Exported {written} sessions."
                        : $"Exported {written} of {toExport.Length} sessions.";
            }
        }
        catch (Exception ex)
        {
            _appLogger.Error(ex, "Mobile: session export failed");
            StatusMessage = "Export failed — see logs.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- Session viewer (#7 phase-2): tap a session → plot its logged data ----

    /// <summary>Opens the viewer overlay for a session and loads its samples into an OxyPlot model
    /// off the UI thread (reusing the desktop <see cref="SessionDataRepository"/> +
    /// <see cref="PlotModelFactory"/>). Small sessions plot their full initial batch; large ones use
    /// the full-range downsampled load (~3000 pts/channel) so the phone never materializes millions
    /// of rows. Failures and empty sessions surface on the viewer status line, never thrown.</summary>
    [RelayCommand]
    private async Task ViewSession(LoggingSession? session)
    {
        if (session is null || IsViewerLoading) { return; }

        // Open immediately with a loading state; the DB read + plot build run off-thread below.
        ViewerTitle = session.Name;
        ViewerChannels.Clear();
        SessionPlotModel = null;
        ViewerStatus = "Loading…";
        IsViewerOpen = true;
        IsViewerLoading = true;
        try
        {
            var repo = Repo();
            if (repo is null)
            {
                ViewerStatus = "Logged data isn't available on this device.";
                return;
            }

            var built = await Task.Run(() => BuildSessionPlot(repo, session.ID));
            // Marshal the plot-model + legend assignment onto the UI thread: SessionPlotModel is
            // bound to the PlotView and ViewerChannels to the legend, and the await may not resume
            // on the UI thread.
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // The user may have closed the viewer (or opened a different session) while this load
                // ran off-thread — don't clobber that newer state with this stale result.
                if (!IsViewerOpen) { return; }

                if (built is null)
                {
                    ViewerStatus = "This session has no data to plot.";
                    return;
                }

                foreach (var channel in built.Value.channels) { ViewerChannels.Add(channel); }
                SessionPlotModel = built.Value.model;
                ViewerStatus = string.Empty;
            });
        }
        catch (Exception ex)
        {
            _appLogger.Error(ex, "Mobile: failed to load session plot");
            ViewerStatus = "Couldn't load this session's data. See logs.";
        }
        finally
        {
            IsViewerLoading = false;
        }
    }

    /// <summary>Closes the viewer overlay and releases the plot model + legend so a large session's
    /// points don't linger in memory after the viewer is dismissed.</summary>
    [RelayCommand]
    private void CloseViewer()
    {
        IsViewerOpen = false;
        SessionPlotModel = null;
        ViewerChannels.Clear();
        ViewerStatus = string.Empty;
    }

    /// <summary>Builds a styled OxyPlot model (analog/digital/time axes, dark theme) with one line
    /// series per channel from a session's samples, plus the matching legend chips. Runs entirely on
    /// a background thread — the OxyPlot objects are unattached POCOs until assigned to the bound
    /// property. Returns null for a session that yields no plottable channel. </summary>
    private (PlotModel model, List<ViewerChannel> channels)? BuildSessionPlot(SessionDataRepository repo, int sessionId)
    {
        var initial = repo.LoadInitialSession(sessionId);
        if (initial.IsEmpty) { return null; }

        var points = initial.Points;
        // The Phase-1 initial batch is capped and only covers the START of a large session, so for
        // sessions past the cap pull a uniformly-sampled full-range view instead (same two-phase
        // strategy the desktop uses). Keep the initial batch if the full-range load found nothing.
        if (initial.TotalSampleCount > SessionDataRepository.INITIAL_LOAD_POINTS)
        {
            var seeded = new Dictionary<(string deviceSerial, string channelName), List<DataPoint>>();
            foreach (var channel in initial.Channels)
            {
                seeded[(channel.DeviceSerialNo, channel.ChannelName)] = new List<DataPoint>();
            }

            var firstTime = repo.LoadSampledData(sessionId, initial.Channels.Count, seeded);
            if (firstTime is not null && seeded.Values.Any(p => p.Count > 0))
            {
                points = seeded;
            }
            else if (_factory is not null)
            {
                // Degenerate case: every sample shares one timestamp, so LoadSampledData can't
                // sample a zero-width time range and returns null. Fall back to per-channel MIN/MAX
                // value spreads aggregated across the FULL session (mirrors the desktop) so the plot
                // shows the true value range rather than only the capped initial batch's first rows.
                var channelKeys = initial.Channels
                    .Select(c => (c.DeviceSerialNo, c.ChannelName))
                    .ToList();
                var spreads = SessionDataRepository.LoadSingleTickValueSpread(_factory, sessionId, channelKeys);
                if (spreads.Values.Any(p => p.Count > 0)) { points = spreads; }
            }
        }

        var factory = new PlotModelFactory();
        var model = factory.CreateMainPlotModel();
        var legend = new List<ViewerChannel>();
        foreach (var channel in initial.Channels)
        {
            if (!points.TryGetValue((channel.DeviceSerialNo, channel.ChannelName), out var channelPoints)
                || channelPoints.Count == 0)
            {
                continue;
            }

            // databaseLogger: null → no minimap sync (there is no minimap on mobile); the series
            // still gets the correct Analog/Digital Y axis + colour from the channel.
            var (series, _) = factory.CreateChannelSeries(
                channel.ChannelName, channel.DeviceSerialNo, channel.Type, channel.Color, model, null);
            series.ItemsSource = channelPoints;
            model.Series.Add(series);
            legend.Add(new ViewerChannel(channel.ChannelName, channel.Color));
        }

        return model.Series.Count == 0 ? null : (model, legend);
    }

    // ---- Delete (#7 phase-2): per-session + delete-all, each behind a confirm ----

    /// <summary>Deletes one session after a destructive confirm. The DB delete is transactional and
    /// rethrows on failure (<see cref="SessionDataRepository.DeleteSession"/>), so the bound row is
    /// removed only on success — a failed delete keeps the row rather than hiding data that survived.</summary>
    [RelayCommand]
    private async Task DeleteSession(LoggingSession? session)
    {
        if (session is null || IsBusy) { return; }

        var confirmed = await ConfirmOverlay.ShowAsync(
            "Delete session?",
            $"Delete “{session.Name}”? This permanently removes its logged data and can't be undone.",
            affirmativeLabel: "DELETE", isDestructive: true);
        if (!confirmed) { return; }

        // Re-verify no logging is active before touching storage — it may have started while the
        // confirm was open, and a delete must not race an in-flight logging session's writes.
        if (LoggingManager.Instance.Active)
        {
            StatusMessage = "Stop logging before deleting a session.";
            return;
        }

        var repo = Repo();
        if (repo is null) { StatusMessage = "Delete isn't available on this device."; return; }

        IsBusy = true;
        try
        {
            await Task.Run(() => repo.DeleteSession(session));
            Sessions.Remove(session);
            StatusMessage = "Session deleted.";
            OnPropertyChanged(nameof(HasSessions));
            OnPropertyChanged(nameof(HasNoSessions));
        }
        catch (Exception ex)
        {
            _appLogger.Error(ex, "Mobile: failed to delete session");
            StatusMessage = "Couldn't delete that session — see logs.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Deletes every listed session after a destructive confirm, refusing while logging is
    /// active (mirrors the desktop guard). Each delete is independent, so a single failure doesn't
    /// abort the rest; only the sessions that actually deleted are removed from the list, and the
    /// reported count is truthful.</summary>
    [RelayCommand]
    private async Task DeleteAll()
    {
        if (IsBusy || Sessions.Count == 0) { return; }

        // Refuse while a logging session is writing — a bulk delete must not race active writes.
        if (LoggingManager.Instance.Active)
        {
            StatusMessage = "Stop logging before deleting all sessions.";
            return;
        }

        // Snapshot the session set BEFORE prompting so the confirmed count matches exactly what gets
        // deleted (the collection could change while the confirm is open).
        var toDelete = Sessions.ToArray();
        var total = toDelete.Length;
        var confirmed = await ConfirmOverlay.ShowAsync(
            "Delete all sessions?",
            $"Delete all {total} logged session{(total == 1 ? "" : "s")}? This permanently removes their data and can't be undone.",
            affirmativeLabel: "DELETE ALL", isDestructive: true);
        if (!confirmed) { return; }

        // Re-verify logging didn't start while the confirm was open.
        if (LoggingManager.Instance.Active)
        {
            StatusMessage = "Stop logging before deleting all sessions.";
            return;
        }

        var repo = Repo();
        if (repo is null) { StatusMessage = "Delete isn't available on this device."; return; }

        IsBusy = true;
        try
        {
            var deleted = await Task.Run(() =>
            {
                var succeeded = new List<LoggingSession>(toDelete.Length);
                foreach (var session in toDelete)
                {
                    try { repo.DeleteSession(session); succeeded.Add(session); }
                    catch (Exception ex) { _appLogger.Error(ex, $"Mobile: delete-all failed for session {session.ID}"); }
                }
                return succeeded;
            });

            foreach (var session in deleted) { Sessions.Remove(session); }
            OnPropertyChanged(nameof(HasSessions));
            OnPropertyChanged(nameof(HasNoSessions));

            StatusMessage = deleted.Count == total
                ? $"Deleted {total} session{(total == 1 ? "" : "s")}."
                : $"Deleted {deleted.Count} of {total}; some couldn't be removed — see logs.";
        }
        catch (Exception ex)
        {
            _appLogger.Error(ex, "Mobile: delete-all failed");
            StatusMessage = "Delete all failed — see logs.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Lazily builds a <see cref="SessionDataRepository"/> over the mobile DI DbContext
    /// factory (the same one <c>App.InitializeMobile</c> registers and <see cref="Reload"/> reads
    /// through). Returns null if the data layer never initialized, so viewer/delete degrade to a
    /// status message instead of throwing.</summary>
    private SessionDataRepository? Repo()
    {
        if (_repository is not null) { return _repository; }

        var provider = Daqifi.Desktop.App.ServiceProvider;
        _factory = provider?.GetService<IDbContextFactory<LoggingContext>>();
        if (_factory is null) { return null; }

        _repository = new SessionDataRepository(_factory, _appLogger);
        return _repository;
    }

    /// <summary>Exports one session; returns true only if the export actually completed and
    /// the destination now holds this run's CSV — so the caller reports a truthful count and
    /// never claims "Exported…" for a failed/partial export or clobbers a good prior file.</summary>
    private static bool ExportOne(OptimizedLoggingSessionExporter exporter, LoggingSession session, string filepath, int sessionIndex, int totalSessions)
    {
        // Export to a TEMP file and move it over the destination ONLY when the export
        // actually completed. TryExportLoggingSession returns a real success signal (unlike
        // the void overload, which swallows a mid-write failure and would leave a non-empty
        // PARTIAL temp). So a failure — whether before the first write or after rows have
        // flushed — cleans up the temp and leaves the destination (a good prior export)
        // untouched, and is never counted as success. The non-empty check is a backstop.
        // The exporter is passed in (one per batch) so the CSV delimiter it captured at
        // construction stays fixed across a whole "Export all" even if Settings is toggled.
        var tmp = filepath + ".exporting";
        try { if (File.Exists(tmp)) { File.Delete(tmp); } } catch { /* best effort */ }

        var exported = exporter.TryExportLoggingSession(
            session, tmp, exportRelativeTime: false,
            new Progress<int>(), CancellationToken.None,
            sessionIndex, totalSessions);

        if (!exported || !File.Exists(tmp) || new FileInfo(tmp).Length == 0)
        {
            // Export failed (incl. a mid-write error → partial temp) or wrote nothing —
            // leave the destination (which may hold a good prior export) untouched, and
            // clean up any partial/empty temp.
            try { if (File.Exists(tmp)) { File.Delete(tmp); } } catch { /* best effort */ }
            return false;
        }

        try
        {
            File.Move(tmp, filepath, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            // Couldn't place the file (destination locked / permission-denied). Log it (the
            // caller reports failure), keep the destination's prior contents, drop the temp.
            AppLogger.Instance.Error(ex, $"Mobile: export move failed (dest={filepath})");
            try { if (File.Exists(tmp)) { File.Delete(tmp); } } catch { /* best effort */ }
            return false;
        }
    }

    /// <summary>Ensure a unique base file name within a single "export all" by appending
    /// " (2)", " (3)"… on collision, so duplicate or sanitization-colliding session names
    /// don't overwrite one another.</summary>
    private static string UniqueFileName(string baseName, HashSet<string> used)
    {
        var name = baseName;
        var n = 2;
        while (!used.Add(name))
        {
            name = $"{baseName} ({n++})";
        }
        return name;
    }

    /// <summary>A session's display name reduced to a valid file name (a session can be
    /// renamed to arbitrary text), falling back to <c>Session_{id}</c> — mirrors the
    /// desktop ExportDialog's MakeSafeFileName so a bad name never faults the export path.</summary>
    private static string SafeName(LoggingSession session)
    {
        var name = string.IsNullOrWhiteSpace(session.Name) ? $"Session_{session.ID}" : session.Name;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }
        return name;
    }
}

/// <summary>A single entry in the session viewer's legend: the channel name and a colour swatch
/// matching its plotted line. The colour string is the same one stored with the samples and used by
/// <see cref="PlotModelFactory.CreateChannelSeries"/> (an ARGB hex like <c>#FFD32F2F</c>), so the
/// chip and the line always agree; an unparseable value falls back to grey rather than throwing.</summary>
public sealed class ViewerChannel
{
    public string Name { get; }

    public IBrush Swatch { get; }

    public ViewerChannel(string name, string colorHex)
    {
        Name = name;
        IBrush swatch;
        try { swatch = new SolidColorBrush(Color.Parse(colorHex)); }
        catch { swatch = Brushes.Gray; }
        Swatch = swatch;
    }
}
