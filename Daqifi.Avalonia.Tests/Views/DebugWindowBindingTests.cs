using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Guards what makes <c>DebugWindow.axaml</c>'s 20 bindings the XAML compiler's business rather than
/// nobody's (issue #327). The compiler owns the member names; what it cannot see is the declarations
/// being dropped, or an escape hatch reopened beneath them.
/// </summary>
public class DebugWindowBindingTests
{
    private const string View = "Daqifi.Avalonia/Daqifi.Desktop/View/DebugWindow.axaml";

    /// <summary>
    /// Delete <c>x:CompileBindings</c> and all 20 bindings go back on reflection with every head still
    /// green: <c>x:DataType</c> alone changes nothing while <c>AvaloniaUseCompiledBindingsByDefault</c>
    /// is <c>false</c> (#326). Parsed, not searched: the comment in the view names both attributes.
    /// </summary>
    [Theory]
    [InlineData("DataType", "vm:DaqifiViewModel")]
    [InlineData("CompileBindings", "True")]
    public void The_window_declares_its_data_type(string attribute, string value) =>
        BindingFacts.AssertRootDeclares(View, attribute, value);

    [Fact]
    public void The_view_opens_no_escape_hatch() => BindingFacts.AssertNoEscapeHatch(View);
}
