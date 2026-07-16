using System;
using System.Reflection;
using Avalonia.Controls;
using Avalonia.Threading;

namespace Daqifi.Avalonia.Views;

public partial class MobileShellView : UserControl
{
    private readonly MobileShellViewModel _viewModel = new();
    private readonly DispatcherTimer _renderTimer;

    public MobileShellView()
    {
        InitializeComponent();
        DataContext = _viewModel;
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "dev";
        // informational version carries "+<full-sha>" — keep it phone-width
        var plus = version.IndexOf('+');
        var sha = plus >= 0 && version.Length > plus + 8
            ? $" ({version.Substring(plus + 1, 7)})" : "";
        VersionText.Text = $"v{(plus >= 0 ? version[..plus] : version)}{sha}";

        // Redraw the strip-chart at ~20 fps while streaming — decoupled from
        // the 100 Hz sample rate so the UI thread isn't flooded.
        Plot.Series = _viewModel.Series;
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _renderTimer.Tick += (_, _) =>
        {
            if (_viewModel.IsStreaming)
            {
                _viewModel.PollActiveSamples();
                Plot.SampleCount = _viewModel.TotalSamples;
                Plot.Pulse();
            }
        };
        _renderTimer.Start();

        DetachedFromVisualTree += (_, _) =>
        {
            _renderTimer.Stop();
            _viewModel.Dispose();
        };
    }
}
