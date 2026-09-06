using Daqifi.Desktop.Logger;
using OxyPlot;
using OxyPlot.Axes;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Issue #251: with no session selected, the Logged Data pane's plot region was a featureless black
/// rectangle — roughly the upper 57% of the window with no axes, no gridlines and no copy, while the
/// list below it correctly said NO SESSIONS. The fix stops hiding the <c>PlotView</c> when there is
/// nothing to plot, so the region draws the same empty labelled frame the Live Graph pane already
/// draws in the identical state.
///
/// <para>The swap itself is XAML and is verified by the parity-audit capture, not here: views in this
/// repo carry no <c>x:DataType</c>, so <c>IsVisible</c> bindings resolve by reflection at run time and
/// no test in this project can see them. What IS testable, and the whole reason the fix is safe, is
/// the ASSUMPTION the always-visible view now rests on — that the model
/// <see cref="DatabaseLogger"/> hands it at construction is already a complete, labelled, empty plot
/// frame rather than a bare <see cref="PlotModel"/>.</para>
///
/// <para><see cref="DatabaseLogger"/>'s constructor assigns
/// <c>PlotModel = _plotModelFactory.CreateMainPlotModel()</c> unconditionally, before any session is
/// loaded, and nothing adds a series until <c>DisplayLoggingSession</c> runs. So these tests pin the
/// factory's output. Were the axes ever moved out of construction and built on first data instead — a
/// change that would look harmless, break no other test, and still compile and render green on both
/// heads — the pane would silently go back to being the void this issue is about. That is the
/// regression these tests exist to catch, because the only other thing that would catch it is a human
/// looking at a screenshot.</para>
/// </summary>
public class EmptyLoggedPlotFrameTests
{
    private static PlotModel AFreshMainModel() => new PlotModelFactory().CreateMainPlotModel();

    /// <summary>
    /// The frame is empty before a session is chosen — the plot cannot show another session's traces
    /// now that it is drawn rather than collapsed. This is the half that makes always-visible safe.
    /// </summary>
    [Fact]
    public void A_freshly_built_main_model_carries_no_series()
    {
        Assert.Empty(AFreshMainModel().Series);
    }

    /// <summary>
    /// The frame is labelled — these are the three titles the issue names as what Live Graph gets
    /// right and Logged Data did not, and they are what turns an empty region into a legible "nothing
    /// plotted yet" rather than "the plot failed to render".
    /// </summary>
    [Theory]
    [InlineData(PlotModelFactory.ANALOG_AXIS_KEY, "Analog (V)", AxisPosition.Left)]
    [InlineData(PlotModelFactory.DIGITAL_AXIS_KEY, "Digital", AxisPosition.Right)]
    [InlineData(PlotModelFactory.TIME_AXIS_KEY, "Time (ms)", AxisPosition.Bottom)]
    public void A_freshly_built_main_model_carries_the_three_titled_axes(
        string key, string expectedTitle, AxisPosition expectedPosition)
    {
        var axis = Assert.Single(AFreshMainModel().Axes, a => a.Key == key);

        Assert.Equal(expectedTitle, axis.Title);
        Assert.Equal(expectedPosition, axis.Position);
    }

    /// <summary>
    /// Titles alone would leave the interior blank. The analog and time axes carry gridlines, which is
    /// what fills the region and makes it read as a plot; the digital axis deliberately draws none, so
    /// asserting "every axis has gridlines" would be wrong and asserting nothing would miss the point.
    /// </summary>
    [Fact]
    public void The_analog_and_time_axes_draw_the_gridlines_that_fill_the_empty_frame()
    {
        var axes = AFreshMainModel().Axes;

        foreach (var key in new[] { PlotModelFactory.ANALOG_AXIS_KEY, PlotModelFactory.TIME_AXIS_KEY })
        {
            var axis = Assert.Single(axes, a => a.Key == key);
            Assert.Equal(LineStyle.Solid, axis.MajorGridlineStyle);
            Assert.Equal(LineStyle.Solid, axis.MinorGridlineStyle);
        }

        var digital = Assert.Single(axes, a => a.Key == PlotModelFactory.DIGITAL_AXIS_KEY);
        Assert.Equal(LineStyle.None, digital.MajorGridlineStyle);
    }
}
