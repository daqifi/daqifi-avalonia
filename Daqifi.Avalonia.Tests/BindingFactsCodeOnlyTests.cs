using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// Pins <see cref="BindingFacts.CodeOnly"/>, which the dialog call-site guard relies on to count
/// braces and find declarations in code only. Each case is a shape that fooled the raw text: a brace
/// in a comment or literal throws off the scope walk, and a commented-out construction reads as real
/// (Qodo, second shepherd round on PR #376). Offsets must survive, because the guard matches in the
/// blanked text and walks the same indices.
/// </summary>
public class BindingFactsCodeOnlyTests
{
    [Theory]
    [InlineData("a /* { */ b", "ab")]
    [InlineData("var s = \"{\"; }", "vars=;}")]
    [InlineData("$\"x{(y ? \"}\" : \"{\")}z\" {", "{")]
    [InlineData("// var vm = new Vm(\ncode{", "\ncode{")]
    [InlineData("// var vm = new Vm(\r\ncode{", "\r\ncode{")]
    [InlineData("// var vm = new Vm(\rcode{", "\rcode{")]
    [InlineData("@\"a\"\"{\" {", "{")]
    [InlineData("'{' '\\'' {", "{")]
    [InlineData("\"\"\"\n{ \" }\n\"\"\" }", "\n\n}")]
    public void Comments_and_literals_are_blanked_and_offsets_kept(string source, string codeLeft)
    {
        var code = BindingFacts.CodeOnly(source);

        Assert.Equal(source.Length, code.Length);
        Assert.Equal(codeLeft, code.Replace(" ", string.Empty));
    }
}
