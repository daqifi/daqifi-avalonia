using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Guards what makes <c>MobileMainView.axaml</c>'s 12 bindings the XAML compiler's business rather
/// than nobody's (issue #327). The view has no view model of its own: each of its three bound
/// subtrees declares its own <c>x:DataType</c>, and the root declares only <c>x:CompileBindings</c>.
/// Dropping a subtree's <c>x:DataType</c> is a build error under that root, so the compiler owns the
/// scopes and the member names; what it cannot see is the root declaration being dropped, or an
/// escape hatch reopened beneath it.
/// </summary>
public class MobileMainViewBindingTests
{
    private const string View = "Daqifi.Avalonia/Views/MobileMainView.axaml";

    /// <summary>
    /// Delete <c>x:CompileBindings</c> and all 12 bindings go back on reflection with every head still
    /// green: the three subtree <c>x:DataType</c>s alone change nothing while
    /// <c>AvaloniaUseCompiledBindingsByDefault</c> is <c>false</c> (#326). Parsed, not searched: the
    /// comment in the view names the attribute.
    /// </summary>
    [Fact]
    public void The_view_compiles_its_bindings() =>
        BindingFacts.AssertRootDeclares(View, "CompileBindings", "True");

    [Fact]
    public void The_view_opens_no_escape_hatch() => BindingFacts.AssertNoEscapeHatch(View);
}
