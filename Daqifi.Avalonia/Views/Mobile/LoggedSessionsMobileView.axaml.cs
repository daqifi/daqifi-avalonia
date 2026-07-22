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
    }

    // The VM owns the export logic but not the platform picker; the view (which has
    // the TopLevel) supplies the save-path resolver via StorageProvider — the same
    // cross-platform picker the desktop loggers use.
    private void WireResolver()
    {
        if (DataContext is LoggedSessionsMobileViewModel vm)
        {
            vm.SavePathResolver = SavePathAsync;
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

        return file?.TryGetLocalPath() ?? file?.Path?.LocalPath;
    }
}
