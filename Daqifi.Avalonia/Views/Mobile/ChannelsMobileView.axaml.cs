// Projected by portomatic from Daqifi.Desktop.View.Prototype.ChannelsPanePrototype over shared VM Daqifi.Desktop.ViewModels.ChannelsPaneViewModel.
//
// SKELETON code-behind for a projected mobile view. The binding contract
// it reproduces is fixed by the projection spec; layout is authored by the
// apply loop from the mobile dialect brief.
using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.VisualTree;

namespace Daqifi.Avalonia.Views.Mobile;

public partial class ChannelsMobileView : UserControl
{
    public ChannelsMobileView()
    {
        InitializeComponent();
        // Flow the channel tiles into a responsive grid: 1 column (full-width rows)
        // in portrait, more in landscape (like the desktop tile grid). The tiles
        // STRETCH to the column width (UniformGrid), so the tile's internal Grid
        // always gets the full column and never clips — unlike a fixed tile width.
        SizeChanged += (_, _) => UpdateTileColumns();
    }

    private void UpdateTileColumns()
    {
        var b = Bounds;
        if (b.Width <= 0 || b.Height <= 0) { return; }
        // Portrait → 1 (readable full-width rows). Landscape → fit ~440px columns
        // (2–4 depending on width) so the section uses the horizontal space.
        var columns = b.Width <= b.Height ? 1 : Math.Max(2, (int)(b.Width / 440));
        foreach (var grid in this.GetVisualDescendants().OfType<UniformGrid>())
        {
            if (grid.Tag as string == "tileGrid")
            {
                grid.Columns = columns;
            }
        }
    }
}
