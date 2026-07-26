using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Daqifi.Avalonia.Views.Mobile;

public partial class LoggedSessionsMobileView : UserControl
{
    public LoggedSessionsMobileView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => WireResolver();

        // Navigating away from the Storage pane detaches this cached view. Cancel any pending
        // delete confirm so its awaiter (holding the view model alive) unwinds cleanly, and close
        // the viewer so a large session's plot model is released — mirrors the desktop panes'
        // explicit confirm-cancel on navigation/cleanup.
        DetachedFromVisualTree += (_, _) =>
        {
            if (DataContext is LoggedSessionsMobileViewModel vm)
            {
                vm.ConfirmOverlay.Cancel();
                vm.CloseViewerCommand.Execute(null);
            }
        };
    }

    // The VM owns the export logic but not the platform picker; the view (which has
    // the TopLevel) supplies the resolvers via StorageProvider — the same
    // cross-platform pickers the desktop loggers use. A single session exports to a
    // picked FILE; "Export all" exports one CSV per session into a picked FOLDER
    // (mirrors the desktop, which writes {session}.csv per session into a directory).
    private void WireResolver()
    {
        if (DataContext is LoggedSessionsMobileViewModel vm)
        {
            vm.SavePathResolver = SavePathAsync;
            vm.SaveFolderResolver = SaveFolderAsync;
        }
    }

    private async Task<string?> SavePathAsync(string suggestedName)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) { return null; }

        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export logged data",
            SuggestedFileName = suggestedName,
            DefaultExtension = "csv",
            FileTypeChoices = new[]
            {
                new FilePickerFileType("CSV") { Patterns = new[] { "*.csv" } },
            },
        });

        // Only a real local filesystem path is usable: OptimizedLoggingSessionExporter
        // opens a StreamWriter on it. A content-URI-only pick (TryGetLocalPath == null,
        // e.g. a cloud/SAF location on Android) has no such path, so return null rather
        // than a bogus Path.LocalPath that would fault mid-export. (Stream-based export
        // to arbitrary content URIs is #7 phase 2.)
        return file?.TryGetLocalPath();
    }

    private async Task<string?> SaveFolderAsync()
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null) { return null; }

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Export all sessions to folder",
            AllowMultiple = false,
        });

        // Same local-path constraint as SavePathAsync: the exporter writes each
        // {session}.csv via a StreamWriter on a real path, so a content-URI-only
        // folder pick (TryGetLocalPath == null) is treated as unavailable.
        var folder = folders.Count > 0 ? folders[0] : null;
        return folder?.TryGetLocalPath();
    }
}
