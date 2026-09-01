using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Daqifi.Avalonia.Services;

namespace Daqifi.Avalonia.Views;

/// <summary>
/// Minimal live strip-chart: overlays each channel's rolling buffer as an
/// auto-scaled polyline with a colored legend showing the latest value.
/// Driven by a render timer (see <c>MobileShellView</c>) so the redraw rate
/// is decoupled from the sample rate.
/// </summary>
public sealed class LivePlot : Control
{
    // Render runs off the view's render timer while streaming, so anything constructed inside it
    // is allocated ~10x/second — and with 16 channels the per-series brush and pen made that
    // hundreds of allocations/second of pure churn. These are immutable and shared instead.
    //
    // The per-colour caches are plain Dictionaries with no lock: Render is only ever invoked on
    // the UI thread, so there is no second writer to race with.
    private static readonly IBrush PlotFill = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    private static readonly IBrush Plate = new SolidColorBrush(Color.FromArgb(0xE0, 0x0E, 0x16, 0x1C));
    private static readonly IBrush HeaderText = new SolidColorBrush(Color.FromArgb(0xCC, 0xC9, 0xDA, 0xE8));
    private static readonly Dictionary<uint, IBrush> SeriesBrushes = [];
    private static readonly Dictionary<uint, Pen> SeriesPens = [];

    private static IBrush BrushFor(uint argb)
    {
        if (!SeriesBrushes.TryGetValue(argb, out var brush))
        {
            brush = new SolidColorBrush(Color.FromUInt32(argb));
            SeriesBrushes[argb] = brush;
        }
        return brush;
    }

    private static Pen PenFor(uint argb)
    {
        if (!SeriesPens.TryGetValue(argb, out var pen))
        {
            pen = new Pen(BrushFor(argb), 1.5);
            SeriesPens[argb] = pen;
        }
        return pen;
    }

    // One reusable destination per series for ChannelSeries.CopyTo, plus how many samples each
    // holds. Render used to take a freshly allocated array per series per redraw — 16 x 600
    // doubles, ~77 KB a frame, which at 20 redraws a second made this control the app's largest
    // allocator (#122). Same reasoning as the brush and pen caches above: Render only ever runs
    // on the UI thread, so these need no synchronisation of their own.
    private double[][] _samples = [];
    private int[] _sampleCounts = [];

    public IReadOnlyList<ChannelSeries>? Series { get; set; }

    /// <summary>
    /// Samples acquired since streaming started, summed over every streamed channel.
    /// </summary>
    /// <remarks>
    /// Set from <c>MobileShellViewModel.TotalSamples</c>, which counts every sample the device
    /// delivers at the point the streaming frame is parsed. Until #120 it was incremented by the
    /// 20 Hz render poll instead, so at 16 channels / 100 Hz it showed ~320/s against the ~1,600
    /// the device was actually producing — a fifth of the truth, on a label that says "samples".
    /// <para>
    /// It has never described what is DRAWN. The poll still appends at most one point per channel
    /// per tick, so the trace remains a 20 Hz decimation of the stream and a spike shorter than
    /// 50 ms is still invisible. Min/max decimation would be the fix for that; it was left out of
    /// the #122 render-path work on measurement — the cost there is rasterising the stroke, not
    /// the number of points, and decimating to the plot's width would have added points, not
    /// removed them. So it remains unimplemented. Recorded data was never affected either way:
    /// logging runs off the per-frame device message
    /// (AbstractStreamingDevice.DispatchDeviceMessage), so a CSV export has always held every
    /// sample.
    /// </para>
    /// </remarks>
    public long SampleCount { get; set; }

    /// <summary>Request a redraw (called from the view's render timer).</summary>
    public void Pulse() => InvalidateVisual();

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        ctx.FillRectangle(PlotFill, new Rect(0, 0, w, h), 8);

        var series = Series;
        if (series == null || series.Count == 0) { return; }

        // Shared Y auto-scale across every series so overlaid traces are
        // comparable; copy once per render into the reusable buffers.
        if (_samples.Length < series.Count)
        {
            Array.Resize(ref _samples, series.Count);
            Array.Resize(ref _sampleCounts, series.Count);
        }
        var min = double.MaxValue;
        var max = double.MinValue;
        for (var si = 0; si < series.Count; si++)
        {
            var s = series[si];
            var d = _samples[si];
            if (d == null || d.Length < s.Capacity)
            {
                d = new double[s.Capacity];
                _samples[si] = d;
            }
            var n = s.CopyTo(d);
            _sampleCounts[si] = n;
            for (var i = 0; i < n; i++)
            {
                var v = d[i];
                if (v < min) { min = v; }
                if (v > max) { max = v; }
            }
        }

        // Traces (only when there is data; the legend below always draws so
        // the channel set is visible immediately, even before the first
        // sample arrives).
        var hasData = min <= max;
        if (hasData)
        {
            if (max - min < 1e-9) { max = min + 1; min -= 1; }  // flat line
            for (var si = 0; si < series.Count; si++)
            {
                var d = _samples[si];
                var n = _sampleCounts[si];
                if (n < 2) { continue; }
                var pen = PenFor(series[si].ColorArgb);
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    for (var i = 0; i < n; i++)
                    {
                        var x = w * i / (n - 1);
                        var y = h * (1 - (d[i] - min) / (max - min));
                        var pt = new Point(x, y);
                        if (i == 0) { g.BeginFigure(pt, false); }
                        else { g.LineTo(pt); }
                    }
                }
                ctx.DrawGeometry(null, pen, geo);
            }
        }

        // Both readouts below sit ON TOP of the traces, so each gets an opaque plate first.
        // Without one the text is drawn straight over the waveforms and is unreadable wherever a
        // trace crosses it — at 16 channels that is most of the time (#117). The plate is darker
        // than the plot fill and nearly opaque so any trace colour still reads against it.

        // Header: samples acquired — every sample the device has delivered, not the subset this
        // control drew (see the SampleCount remarks, and #120). "acquired" is spelled out because
        // the trace beneath it is still decimated, and a bare "samples" over a decimated plot is
        // what made the old figure so easy to misread.
        var header = new FormattedText(
            $"{SampleCount:N0} samples acquired", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 13,
            HeaderText);
        // Clamp to the control. Both plates are sized from measured text, so a long enough label
        // — and "samples acquired" is the longest this header has carried — would paint past the
        // right edge on a narrow layout. Nothing local sets ClipToBounds, so it would escape the
        // plot region rather than being cut off.
        ctx.FillRectangle(
            Plate, new Rect(4, h - 25, Math.Min(header.Width + 10, Math.Max(0, w - 8)), header.Height + 6), 5);
        ctx.DrawText(header, new Point(8, h - 22));

        // Legend: channel name + latest value, in the trace's color. Measured in full before
        // anything is drawn, so the plate can be sized to the widest label rather than guessed at.
        var labels = new List<FormattedText>(series.Count);
        var widest = 0.0;
        foreach (var s in series)
        {
            var label = s.HasData
                ? $"{s.Name}: {s.Latest:0.###}"
                : $"{s.Name}: waiting…";
            var text = new FormattedText(
                label, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                Typeface.Default, 13, BrushFor(s.ColorArgb));
            if (text.Width > widest) { widest = text.Width; }
            labels.Add(text);
        }

        const double lineHeight = 18.0;
        ctx.FillRectangle(
            Plate,
            new Rect(4, 2, Math.Min(widest + 10, Math.Max(0, w - 8)), labels.Count * lineHeight + 8),
            5);

        var y0 = 6.0;
        foreach (var text in labels)
        {
            ctx.DrawText(text, new Point(8, y0));
            y0 += lineHeight;
        }
    }
}
