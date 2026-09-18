using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Guards what makes <c>LiveGraphPane.axaml</c>'s 40 bindings the XAML compiler's business rather
/// than nobody's (issue #327). The compiler owns the member names; what it cannot see is the
/// declarations being dropped, or an escape hatch reopened beneath them.
/// </summary>
public class LiveGraphPaneBindingTests
{
    private const string View = "Daqifi.Avalonia/Daqifi.Desktop/View/Prototype/LiveGraphPane.axaml";

    /// <summary>
    /// Delete <c>x:CompileBindings</c> and all 40 bindings go back on reflection with every head still
    /// green: <c>x:DataType</c> alone changes nothing while <c>AvaloniaUseCompiledBindingsByDefault</c>
    /// is <c>false</c> (#326). Parsed, not searched: the comment in the view names both attributes.
    /// </summary>
    [Theory]
    [InlineData("DataType", "vm:DaqifiViewModel")]
    [InlineData("CompileBindings", "True")]
    public void The_pane_declares_its_data_type(string attribute, string value) =>
        BindingFacts.AssertRootDeclares(View, attribute, value);

    /// <summary>
    /// The tracker template's scope is a claim the compiler trusts without checking it against what
    /// <c>PlotBase.ShowTracker</c> actually assigns — a <c>TrackerHitResult</c>. Pinned so a
    /// plausible-looking repoint cannot quietly re-scope the three tracker bindings.
    /// </summary>
    [Fact]
    public void The_tracker_template_is_scoped_to_what_OxyPlot_assigns() =>
        BindingFacts.AssertBinds(View, "<ControlTemplate x:DataType=\"oxycore:TrackerHitResult\">");

    [Fact]
    public void The_view_opens_no_escape_hatch() => BindingFacts.AssertNoEscapeHatch(View);
}
