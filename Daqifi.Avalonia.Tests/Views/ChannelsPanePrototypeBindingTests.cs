using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Guards what makes <c>ChannelsPanePrototype.axaml</c>'s 81 bindings the XAML compiler's business rather
/// than nobody's (issue #327). The compiler owns the member names — including the tile template's, whose
/// resource-dictionary shape infers nothing and so fails the build if its declaration is dropped. What it
/// cannot see is the root declarations being dropped, or an escape hatch reopened beneath them.
/// </summary>
public class ChannelsPanePrototypeBindingTests
{
    private const string View = "Daqifi.Avalonia/Daqifi.Desktop/View/Prototype/ChannelsPanePrototype.axaml";

    /// <summary>
    /// Delete <c>x:CompileBindings</c> and all 81 bindings go back on reflection with every head still
    /// green: <c>x:DataType</c> alone changes nothing while <c>AvaloniaUseCompiledBindingsByDefault</c>
    /// is <c>false</c>. Parsed, not searched: the comment in the view names both attributes.
    /// </summary>
    [Theory]
    [InlineData("DataType", "vm:ChannelsPaneViewModel")]
    [InlineData("CompileBindings", "True")]
    public void The_view_declares_its_data_type(string attribute, string value) =>
        BindingFacts.AssertRootDeclares(View, attribute, value);

    [Fact]
    public void The_view_opens_no_escape_hatch() => BindingFacts.AssertNoEscapeHatch(View);
}
