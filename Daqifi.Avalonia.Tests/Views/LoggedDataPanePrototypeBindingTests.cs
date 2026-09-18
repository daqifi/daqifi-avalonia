using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Guards what makes <c>LoggedDataPanePrototype.axaml</c>'s 102 bindings the XAML compiler's business
/// rather than nobody's (issue #327). The compiler owns the member names; what it cannot see is the
/// declarations being dropped or repointed, or an escape hatch reopened beneath them.
/// </summary>
public class LoggedDataPanePrototypeBindingTests
{
    private const string View = "Daqifi.Avalonia/Daqifi.Desktop/View/Prototype/LoggedDataPanePrototype.axaml";

    /// <summary>
    /// Delete <c>x:CompileBindings</c> and all 102 bindings go back on reflection with every head still
    /// green: <c>x:DataType</c> alone changes nothing while <c>AvaloniaUseCompiledBindingsByDefault</c>
    /// is <c>false</c> (#326). Parsed, not searched: the comment in the view names both attributes.
    /// </summary>
    [Theory]
    [InlineData("DataType", "vm:DaqifiViewModel")]
    [InlineData("CompileBindings", "True")]
    public void The_pane_declares_its_data_type(string attribute, string value) =>
        BindingFacts.AssertRootDeclares(View, attribute, value);

    /// <summary>
    /// Two nested scopes are claims the compiler trusts without checking them against the object the
    /// template will meet: OxyPlot's <c>PlotBase.ShowTracker</c> assigns a <c>TrackerHitResult</c>, and
    /// a <c>ListBoxItem</c>'s DataContext is its item from <c>LoggingSessions</c>. Pinned so a
    /// plausible-looking repoint cannot quietly re-scope them.
    /// </summary>
    [Theory]
    [InlineData("<ControlTemplate x:DataType=\"oxycore:TrackerHitResult\">")]
    [InlineData("<ControlTheme TargetType=\"ListBoxItem\" x:DataType=\"logger:LoggingSession\">")]
    public void The_nested_scopes_name_what_the_runtime_assigns(string declaration) =>
        BindingFacts.AssertBinds(View, declaration);

    [Fact]
    public void The_view_opens_no_escape_hatch() => BindingFacts.AssertNoEscapeHatch(View);
}
