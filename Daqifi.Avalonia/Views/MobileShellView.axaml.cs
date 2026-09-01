using System;
using Avalonia.Controls;
using Avalonia.Threading;
using Daqifi.Desktop.Common;

namespace Daqifi.Avalonia.Views;

public partial class MobileShellView : UserControl
{
    /// <summary>
    /// Polls per plot redraw. The poll runs at 50 ms and must stay there: it is what appends the
    /// plot's points (so the trace's time resolution is the poll rate) and the stream watchdog
    /// counts polls, not seconds, for its ~8 s deadline. Redrawing is the expensive half — the
    /// picture, not the data — and it is what was costing ~1.7 cores on a phone (#122). Drawing
    /// every second poll halves that at no cost to the samples plotted: they are still captured
    /// at 20 Hz, just shown 10 times a second. At the 600-sample buffer the trace advances about
    /// two pixels per redraw either way.
    /// </summary>
    private const int PollsPerRedraw = 2;

    private readonly MobileShellViewModel _viewModel = new();
    private readonly DispatcherTimer _renderTimer;
    private int _pollsSinceRedraw;

    public MobileShellView()
    {
        InitializeComponent();
        DataContext = _viewModel;
        // Resolved by AppVersion, which is also what the Sentry release is now built from (#126),
        // so the version a user reads off this screen and the version a crash report is tagged
        // with cannot disagree.
        //
        // InformationalVersion carries "+<full-sha>". Show a clean "vX.Y.Z" to users (matches the
        // desktop "DAQIFI VX.Y.Z" — a git SHA is dev noise, not user-facing content, #13). Debug
        // builds keep the short SHA appended to aid on-device build verification.
        var semver = AppVersion.Semantic ?? "dev";
#if DEBUG
        var sha = AppVersion.ShortBuildMetadata is { } metadata ? $" ({metadata})" : "";
#else
        var sha = "";
#endif
        VersionText.Text = $"v{semver}{sha}";

        // Poll the device at ~20 Hz while streaming — decoupled from the 100 Hz sample rate so
        // the UI thread isn't flooded — and redraw the strip-chart on every second poll (~10 fps).
        Plot.Series = _viewModel.Series;
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        // NOTE (#113): this timer keeps firing at its full rate while the Android activity is
        // stopped — measured on a Galaxy A16 / Android 16, ticks exactly 1.000 s apart with no gap
        // across a 45 s background window. It is NOT suspended with the activity. The stream
        // watchdog in MobileShellViewModel depends on knowing this.
        _renderTimer.Tick += (_, _) =>
        {
            if (!_viewModel.IsStreaming) { return; }
            _viewModel.PollActiveSamples();
            _pollsSinceRedraw++;
            if (_pollsSinceRedraw < PollsPerRedraw) { return; }
            _pollsSinceRedraw = 0;
            Plot.SampleCount = _viewModel.TotalSamples;
            Plot.Pulse();
        };
        _renderTimer.Start();

        DetachedFromVisualTree += (_, _) =>
        {
            _renderTimer.Stop();
            _viewModel.Dispose();
        };
    }
}
