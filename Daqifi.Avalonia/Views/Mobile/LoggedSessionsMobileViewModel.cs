using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Exporter;
using Daqifi.Desktop.Logger;

namespace Daqifi.Avalonia.Views.Mobile;

/// <summary>
/// Mobile "APP LOGS" half of the Storage tab — the phone projection of the desktop
/// Logged Data pane's locally-logged session list (#7). Reads the same
/// <see cref="LoggingManager.Instance"/> session collection the desktop pane binds
/// to (populated on mobile by <c>App.InitializeMobile</c>) and offers the
/// non-destructive **export** action via the reused
/// <see cref="OptimizedLoggingSessionExporter"/> + the platform save picker.
///
/// Deferred vs desktop (need mobile-specific dialog/plot infra, tracked on #7):
/// the inline session PLOT VIEWER (desktop pushes to a shared OxyPlot host the
/// mobile shell doesn't run) and DELETE / DELETE-ALL (need a mobile confirm dialog;
/// the desktop MessageBoxService is main-window-bound). Export is non-destructive
/// and needs only the cross-platform StorageProvider picker, so it ships now.
/// </summary>
public partial class LoggedSessionsMobileViewModel : ObservableObject
{
    private readonly IAppLogger _appLogger = AppLogger.Instance;

    /// <summary>Set by the view (which owns the TopLevel): resolves a save path for
    /// a suggested filename, or null if the user cancels.</summary>
    public Func<string, Task<string?>>? SavePathResolver { get; set; }

    public ObservableCollection<LoggingSession> Sessions { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSessions))]
    [NotifyPropertyChangedFor(nameof(HasNoSessions))]
    private bool _loaded;

    public bool HasSessions => Sessions.Count > 0;
    public bool HasNoSessions => Loaded && Sessions.Count == 0;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusMessage = string.Empty;

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
            // Always hydrate straight from the DB, OFF the UI thread. The desktop
            // hydrates once at app-init (when the collection is empty); the mobile
            // shell has no such step, so this VM is the only hydration point — a
            // Count==0 guard would let a session logged this run mask the older
            // persisted history. LoadPersistedLoggingSessions returns a fresh list
            // (it does not touch the shared singleton collection), and its EF Core
            // queries run on a background thread so opening Storage never blocks.
            var loaded = await Task.Run(() => LoggingManager.Instance.LoadPersistedLoggingSessions());
            Sessions.Clear();
            foreach (var session in loaded)
            {
                Sessions.Add(session);
            }
        }
        catch (Exception ex)
        {
            _appLogger.Error(ex, "Mobile: failed to load logged sessions");
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

    /// <summary>Exports one session (when <paramref name="single"/> is non-null) or every
    /// session (when null) to a user-picked CSV file. Non-destructive; failures are logged
    /// and surfaced on the status line, never thrown.</summary>
    private async Task ExportSessionsAsync(LoggingSession? single)
    {
        if (SavePathResolver is null) { return; }

        var toExport = single is not null
            ? new[] { single }
            : System.Linq.Enumerable.ToArray(Sessions);
        if (toExport.Length == 0) { return; }

        var suggested = single is not null ? $"{single.Name}.csv" : "daqifi-sessions.csv";
        string? path;
        try
        {
            path = await SavePathResolver(suggested);
        }
        catch (Exception ex)
        {
            _appLogger.Error(ex, "Mobile: export save-picker failed");
            return;
        }
        if (string.IsNullOrEmpty(path)) { return; }   // user cancelled

        IsBusy = true;
        StatusMessage = "Exporting…";
        try
        {
            await Task.Run(() =>
            {
                var exporter = new OptimizedLoggingSessionExporter();
                var progress = new Progress<int>();
                for (var i = 0; i < toExport.Length; i++)
                {
                    // Export All to a single picked file writes each session sequentially,
                    // mirroring the desktop "Export All" (one file, all sessions).
                    exporter.ExportLoggingSession(
                        toExport[i], path!, exportRelativeTime: false,
                        progress, CancellationToken.None,
                        sessionIndex: i, totalSessions: toExport.Length);
                }
            });
            StatusMessage = toExport.Length == 1
                ? "Exported 1 session."
                : $"Exported {toExport.Length} sessions.";
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
}
