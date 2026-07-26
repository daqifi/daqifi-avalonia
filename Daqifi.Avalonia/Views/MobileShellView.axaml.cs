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
        var informational = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "dev";
        // InformationalVersion carries "+<full-sha>". Show a clean "vX.Y.Z" to users (matches the
        // desktop "DAQIFI VX.Y.Z" — a git SHA is dev noise, not user-facing content, #13). Debug
        // builds keep the short SHA appended to aid on-device build verification.
        var plus = informational.IndexOf('+');
        var semver = plus >= 0 ? informational[..plus] : informational;
#if DEBUG
        // Append up to the first 7 chars of the +<sha> build metadata (git SHAs are
        // >= 7 chars; tolerate shorter/absent metadata without an off-by-one or throw).
        var suffix = plus >= 0 ? informational[(plus + 1)..] : "";
        var sha = suffix.Length > 0 ? $" ({suffix[..Math.Min(7, suffix.Length)]})" : "";
#else
        var sha = "";
#endif
        VersionText.Text = $"v{semver}{sha}";

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
