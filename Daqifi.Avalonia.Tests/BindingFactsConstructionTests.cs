using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// Pins <see cref="BindingFacts.IsWholeConstruction"/>, which decides whether a local the dialog
/// guard found as <c>var x = new VM(</c> actually holds the new <c>VM</c>. The prefix alone let
/// <c>new VM(…).ToString()</c> through with every dialog guard passing (issue #388). Each case is
/// the text from that <c>(</c> on, as <see cref="BindingFacts.CodeOnly"/> would leave it.
/// </summary>
public class BindingFactsConstructionTests
{
    [Theory]
    [InlineData("(a, b);")]
    [InlineData("();")]
    [InlineData("(\n    a,\n    Pick(b, c),\n    () => { d(); });")]
    [InlineData("(a) ;")]
    [InlineData("(a)\n        {\n            X = f(1),\n        };")]
    [InlineData("() { 1, 2 };")]
    public void A_construction_that_ends_the_initializer_holds_the_view_model(string fromParen)
    {
        Assert.True(BindingFacts.IsWholeConstruction(fromParen, 0));
    }

    [Theory]
    [InlineData("(a, b).ToString();")]
    [InlineData("(a) .ToString();")]
    [InlineData("(a)?.Other;")]
    [InlineData("(a) ?? other;")]
    [InlineData("(a)!;")]
    [InlineData("(a) as object;")]
    [InlineData("(a) is { } ? x : y;")]
    [InlineData("(a) with { X = 1 };")]
    [InlineData("(a) { X = 1 }.ToString();")]
    [InlineData("(a) { X = 1 } ?? other;")]
    [InlineData("(Pick(a), b")]
    [InlineData("(a) { X = 1 ")]
    [InlineData("(a)")]
    public void Anything_after_the_construction_is_not_one(string fromParen)
    {
        Assert.False(BindingFacts.IsWholeConstruction(fromParen, 0));
    }
}
