using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Guards what makes <c>ErrorDialog.axaml</c>'s one binding the XAML compiler's business rather than
/// nobody's (issue #327). The compiler owns the member name; what it cannot see is the declarations
/// being dropped, or an escape hatch reopened beneath them.
/// </summary>
public class ErrorDialogBindingTests
{
    private const string View = "Daqifi.Avalonia/Daqifi.Desktop/View/ErrorDialog.axaml";

    /// <summary>
    /// Delete <c>x:CompileBindings</c> and the binding goes back on reflection with every head still
    /// green: <c>x:DataType</c> alone changes nothing while <c>AvaloniaUseCompiledBindingsByDefault</c>
    /// is <c>false</c> (#326). Parsed, not searched: the comment in the view names both attributes.
    /// </summary>
    [Theory]
    [InlineData("DataType", "vm:ErrorDialogViewModel")]
    [InlineData("CompileBindings", "True")]
    public void The_dialog_declares_its_data_type(string attribute, string value) =>
        BindingFacts.AssertRootDeclares(View, attribute, value);

    [Fact]
    public void The_view_opens_no_escape_hatch() => BindingFacts.AssertNoEscapeHatch(View);
}
