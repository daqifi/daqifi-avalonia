using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
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

                await Task.Run(() => ExportOne(single, path!, sessionIndex: 0, totalSessions: 1));
                StatusMessage = "Exported 1 session.";
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

                await Task.Run(() =>
                {
                    for (var i = 0; i < toExport.Length; i++)
                    {
                        var filepath = Path.Combine(folder!, $"{SafeName(toExport[i])}.csv");
                        ExportOne(toExport[i], filepath, sessionIndex: i, totalSessions: toExport.Length);
                    }
                });
                StatusMessage = $"Exported {toExport.Length} sessions.";
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

    private static void ExportOne(LoggingSession session, string filepath, int sessionIndex, int totalSessions)
    {
        var exporter = new OptimizedLoggingSessionExporter();
        exporter.ExportLoggingSession(
            session, filepath, exportRelativeTime: false,
            new Progress<int>(), CancellationToken.None,
            sessionIndex, totalSessions);
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
