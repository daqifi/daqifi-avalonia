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
    public IReadOnlyList<ChannelSeries>? Series { get; set; }

    /// <summary>Total samples appended — a live "is data flowing?" readout.</summary>
    public long SampleCount { get; set; }

    /// <summary>Request a redraw (called from the view's render timer).</summary>
    public void Pulse() => InvalidateVisual();

    public override void Render(DrawingContext ctx)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        ctx.FillRectangle(
            new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)),
            new Rect(0, 0, w, h), 8);

        var series = Series;
        if (series == null || series.Count == 0) { return; }

        // Shared Y auto-scale across every series so overlaid traces are
        // comparable; snapshot once per render.
        var snaps = new List<double[]>(series.Count);
        var min = double.MaxValue;
        var max = double.MinValue;
        foreach (var s in series)
        {
            var d = s.Snapshot();
            snaps.Add(d);
            foreach (var v in d)
            {
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
            for (var si = 0; si < snaps.Count; si++)
            {
                var d = snaps[si];
                if (d.Length < 2) { continue; }
                var pen = new Pen(
                    new SolidColorBrush(Color.FromUInt32(series[si].ColorArgb)), 1.5);
                var geo = new StreamGeometry();
                using (var g = geo.Open())
                {
                    for (var i = 0; i < d.Length; i++)
                    {
                        var x = w * i / (d.Length - 1);
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
        var plate = new SolidColorBrush(Color.FromArgb(0xE0, 0x0E, 0x16, 0x1C));

        // Header: total samples received — the definitive data-flow readout.
        var header = new FormattedText(
            $"{SampleCount:N0} samples", CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, Typeface.Default, 13,
            new SolidColorBrush(Color.FromArgb(0xCC, 0xC9, 0xDA, 0xE8)));
        ctx.FillRectangle(
            plate, new Rect(4, h - 25, header.Width + 10, header.Height + 6), 5);
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
                Typeface.Default, 13, new SolidColorBrush(Color.FromUInt32(s.ColorArgb)));
            if (text.Width > widest) { widest = text.Width; }
            labels.Add(text);
        }

        const double lineHeight = 18.0;
        ctx.FillRectangle(
            plate,
            new Rect(4, 2, widest + 10, labels.Count * lineHeight + 8),
            5);

        var y0 = 6.0;
        foreach (var text in labels)
        {
            ctx.DrawText(text, new Point(8, y0));
            y0 += lineHeight;
        }
    }
}
