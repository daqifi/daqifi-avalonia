using System.Xml.Linq;
using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// Pins the value-converter layer to the bindings that actually use it.
///
/// <para>
/// A converter reaches the app through two separate acts: a class implementing
/// <c>IValueConverter</c>/<c>IMultiValueConverter</c>, and an <c>x:Key</c> registration in a resource
/// dictionary (<c>App.axaml</c> for the app-wide ones, a view's own <c>Resources</c> for the local
/// ones). Neither act is checked against a third thing — whether any binding names that key. A
/// converter can therefore be written, registered, instantiated at startup, and never once asked to
/// convert anything, while every head builds green and every other test passes. That is an
/// indirection layer with no caller, and nothing else in this repo can see it.
/// </para>
///
/// <para>
/// <b>This must be answered by PARSING the markup, not by grepping it.</b> Three of this repo's
/// views carry prose comments that name a converter key — <c>ProfilesMobileView.axaml</c> says "the
/// desktop used its NotNullToVis converter here", and three more explain that the WPF
/// <c>BooleanToInverse</c> converter was replaced by Avalonia's <c>!</c> binding negation. A text
/// search reports all of those as usages and gets the answer exactly backwards: the comment
/// documenting a converter's <i>removal</i> reads as evidence of its use. <see cref="XDocument"/>
/// drops comments, so the attribute values walked below are only the ones the XAML loader will
/// actually act on.
/// </para>
/// </summary>
public class ConverterRegistrationTests
{
    private static readonly XNamespace XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly XName KeyAttribute = XamlNamespace + "Key";

    /// <summary>
    /// Every converter registered under an <c>x:Key</c> is named by at least one binding.
    ///
    /// <para>
    /// The failure this guards is cheap to create and invisible once created: delete the last binding
    /// that used a converter — because Avalonia expresses the same thing natively, or because the
    /// screen went away — and the class, its file and its registration all survive with nothing
    /// pointing at them. Re-adding an orphan registration to <c>App.axaml</c> is the mutation that
    /// reddens this row.
    /// </para>
    /// </summary>
    [Fact]
    public void Every_registered_converter_is_named_by_at_least_one_binding()
    {
        var markup = ParseAllMarkup();

        var orphans = markup.Registrations
            .Where(r => !markup.ConverterKeys.Contains(r.Key))
            .Select(r => $"{r.Key} ({r.TypeName}, registered in {r.File})")
            .OrderBy(text => text, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            orphans.Count == 0,
            "Converter registrations that no binding names:" +
            Environment.NewLine + string.Join(Environment.NewLine, orphans));
    }

    /// <summary>
    /// The positive control for the row above, and the reason it is a separate test rather than a
    /// comment.
    ///
    /// <para>
    /// <see cref="Every_registered_converter_is_named_by_at_least_one_binding"/> passes when the
    /// orphan list is empty — including when it is empty because the walk found no markup, no
    /// registrations, or no bindings at all. A broken path, a renamed directory or an
    /// <see cref="XDocument"/> quirk would turn it into a test that cannot fail and looks exactly
    /// like a test that passed. These lower bounds are deliberately far below the real counts at the
    /// time of writing (32 views, 10 registrations, 9 of them referenced), so ordinary churn does not
    /// touch them and a collapsed walk still cannot slip through.
    /// </para>
    /// </summary>
    [Fact]
    public void The_markup_walk_finds_markup_registrations_and_references()
    {
        var markup = ParseAllMarkup();

        Assert.True(markup.FileCount >= 20, $"Only {markup.FileCount} .axaml files were walked.");
        Assert.True(markup.Registrations.Count >= 6, $"Only {markup.Registrations.Count} converter registrations were found.");
        Assert.True(markup.ConverterKeys.Count >= 6, $"Only {markup.ConverterKeys.Count} keys were found in a converter position.");
    }

    /// <summary>
    /// The negative control for <see cref="ConverterKeysIn"/>: a resource key that appears somewhere
    /// other than a converter position must not count as converter usage.
    ///
    /// <para>
    /// Without this, the guard above degrades into "is this key mentioned anywhere", and a converter
    /// whose last binding was deleted stays green forever as long as an unrelated brush, theme or
    /// parameter happens to share its key — the orphan surviving behind a lookup that has nothing to
    /// do with it. Raised by review on this PR; the first version of the walk had exactly that hole.
    /// </para>
    /// </summary>
    [Theory]
    // Converter positions — collected.
    [InlineData("Converter", "{StaticResource Ghost}", true)]
    [InlineData("Converter", "{DynamicResource Ghost}", true)]
    [InlineData("Converter", "{ StaticResource Ghost }", true)]
    [InlineData("IsVisible", "{Binding Thing, Converter={StaticResource Ghost}}", true)]
    [InlineData("IsVisible", "{Binding Thing, Converter={StaticResource Ghost}, Mode=OneWay}", true)]
    [InlineData("Text", "{MultiBinding Converter={StaticResource Ghost}}", true)]
    [InlineData("IsVisible", "{Binding Thing,Converter={StaticResource Ghost}}", true)]
    // A converter on an arbitrary property is still a converter — do not over-tighten.
    [InlineData("Tag", "{Binding Thing, Converter={StaticResource Ghost}}", true)]
    // Everything else — not collected.
    [InlineData("Background", "{DynamicResource Ghost}", false)]
    [InlineData("Fill", "{StaticResource Ghost}", false)]
    [InlineData("Theme", "{StaticResource Ghost}", false)]
    [InlineData("IsVisible", "{Binding Thing, ConverterParameter={StaticResource Ghost}}", false)]
    [InlineData("ToolTip.Tip", "see the Ghost resource", false)]
    [InlineData("Content", "Ghost", false)]
    // A longer property name that merely ENDS in 'Converter' is not a converter clause.
    [InlineData("IsVisible", "{Binding Thing, SomeConverter={StaticResource Ghost}}", false)]
    [InlineData("IsVisible", "{Binding Thing, ValueConverter={StaticResource Ghost}}", false)]
    // Literal attribute text is not a markup extension, however much it looks like one.
    [InlineData("Tag", "Converter={StaticResource Ghost}", false)]
    [InlineData("ToolTip.Tip", "pass Converter={StaticResource Ghost} to the binding", false)]
    public void Only_a_converter_position_counts_as_converter_usage(string attribute, string value, bool expected)
    {
        Assert.Equal(expected, ConverterKeysIn(attribute, value).Contains("Ghost"));
    }

    private sealed record Registration(string Key, string TypeName, string File);

    private sealed record Markup(int FileCount, IReadOnlyList<Registration> Registrations, IReadOnlySet<string> ConverterKeys);

    private static Markup ParseAllMarkup()
    {
        var root = BindingFacts.RepoRoot();
        var registrations = new List<Registration>();
        var referenced = new HashSet<string>(StringComparer.Ordinal);
        var fileCount = 0;

        foreach (var path in Directory.EnumerateFiles(root, "*.axaml", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) ||
                relative.Contains("/bin/", StringComparison.Ordinal) ||
                relative.StartsWith("third_party/", StringComparison.Ordinal))
            {
                continue;
            }

            fileCount++;
            var document = XDocument.Parse(File.ReadAllText(path));
            foreach (var element in document.Descendants())
            {
                var typeName = element.Name.LocalName;
                var key = element.Attribute(KeyAttribute)?.Value;
                if (key is not null && typeName.EndsWith("Converter", StringComparison.Ordinal))
                {
                    registrations.Add(new Registration(key, typeName, relative));
                }

                foreach (var attribute in element.Attributes())
                {
                    if (attribute.Name == KeyAttribute)
                    {
                        continue;
                    }

                    foreach (var converterKey in ConverterKeysIn(attribute.Name.LocalName, attribute.Value))
                    {
                        referenced.Add(converterKey);
                    }
                }
            }
        }

        return new Markup(fileCount, registrations, referenced);
    }

    /// <summary>
    /// The resource keys one attribute puts in a <b>converter position</b>, and only those.
    ///
    /// <para>
    /// The distinction matters: a resource key is not evidence that a <i>converter</i> is used, it is
    /// evidence that <i>something</i> with that key is used. XAML resources share one key namespace,
    /// so a brush, a control theme and a converter can collide there; if this collected every
    /// <c>{StaticResource X}</c> from every attribute, a <c>Background="{DynamicResource Foo}"</c>
    /// would keep a converter registered as <c>Foo</c> looking alive after its last binding went
    /// away — which is precisely the failure this file exists to catch. Only two positions actually
    /// hand a converter to the binding engine, and only those two are counted:
    /// </para>
    ///
    /// <list type="bullet">
    /// <item>the element-syntax attribute — <c>&lt;Binding Converter="{StaticResource Key}"/&gt;</c>,
    /// also how <c>MultiBinding</c> is written in this repo;</item>
    /// <item>the nested <c>Converter=</c> clause inside a markup extension —
    /// <c>"{Binding Path, Converter={StaticResource Key}}"</c>.</item>
    /// </list>
    ///
    /// <para>
    /// <c>ConverterParameter=</c> is deliberately <b>not</b> a converter position, and the
    /// <c>=</c>-after-<c>Converter</c> requirement below is what excludes it: a parameter names a
    /// value handed <i>to</i> a converter, not the converter itself.
    /// </para>
    /// </summary>
    internal static IReadOnlyCollection<string> ConverterKeysIn(string attributeLocalName, string attributeValue)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);

        // <Binding Converter="{StaticResource Key}"/> — the whole attribute value is the converter.
        if (string.Equals(attributeLocalName, "Converter", StringComparison.Ordinal))
        {
            var cursor = 0;
            SkipTo(attributeValue, ref cursor, '{');
            ReadResourceKeyInto(attributeValue, cursor, keys);
        }

        // "{Binding X, Converter={StaticResource Key}}" — the nested clause.
        //
        // Two boundaries, and both are load-bearing. The value must BE a markup extension, because a
        // nested Converter= clause cannot exist outside one — that rules out literal attribute text
        // such as Tag="Converter={StaticResource Ghost}", which begins at offset 0 and would satisfy
        // any preceding-character rule. And the token must start at a property boundary, which rules
        // out a longer property name ending in 'Converter' (SomeConverter=, ValueConverter=).
        if (attributeValue.TrimStart().StartsWith('{'))
        {
            CollectNestedConverterKeys(attributeValue, keys);
        }

        return keys;
    }

    private static void CollectNestedConverterKeys(string attributeValue, HashSet<string> keys)
    {
        const string converter = "Converter";
        for (var index = 0; index + converter.Length < attributeValue.Length; index++)
        {
            if (!Match(attributeValue, index, converter) || !IsPropertyBoundary(attributeValue, index))
            {
                continue;
            }

            var cursor = index + converter.Length;
            while (cursor < attributeValue.Length && attributeValue[cursor] == ' ')
            {
                cursor++;
            }

            // 'ConverterParameter=' fails here on 'P', which is the whole point.
            if (cursor >= attributeValue.Length || attributeValue[cursor] != '=')
            {
                continue;
            }

            cursor++;
            SkipTo(attributeValue, ref cursor, '{');
            ReadResourceKeyInto(attributeValue, cursor, keys);
        }
    }

    /// <summary>
    /// True when a markup-extension property name could start at <paramref name="index"/> — i.e. it
    /// is preceded by the extension's opening brace, a clause comma, or whitespace. This is what
    /// stops <c>SomeConverter=</c> from being read as <c>Converter=</c>.
    /// </summary>
    private static bool IsPropertyBoundary(string text, int index) =>
        index == 0 || text[index - 1] is '{' or ',' || char.IsWhiteSpace(text[index - 1]);

    /// <summary>Advances past spaces and one optional opening brace.</summary>
    private static void SkipTo(string text, ref int cursor, char optionalOpener)
    {
        while (cursor < text.Length && text[cursor] == ' ')
        {
            cursor++;
        }

        if (cursor < text.Length && text[cursor] == optionalOpener)
        {
            cursor++;
        }

        while (cursor < text.Length && text[cursor] == ' ')
        {
            cursor++;
        }
    }

    /// <summary>
    /// Reads a <c>StaticResource Key</c> / <c>DynamicResource Key</c> starting exactly at
    /// <paramref name="cursor"/> — anchored, not searched, so a key can only be collected from the
    /// position the caller already established is a converter position.
    /// </summary>
    private static void ReadResourceKeyInto(string text, int cursor, HashSet<string> into)
    {
        const string staticResource = "StaticResource";
        const string dynamicResource = "DynamicResource";

        var length = Match(text, cursor, staticResource) ? staticResource.Length
            : Match(text, cursor, dynamicResource) ? dynamicResource.Length
            : 0;
        if (length == 0)
        {
            return;
        }

        cursor += length;
        while (cursor < text.Length && text[cursor] == ' ')
        {
            cursor++;
        }

        var start = cursor;
        while (cursor < text.Length &&
               (char.IsLetterOrDigit(text[cursor]) || text[cursor] is '_' or '.'))
        {
            cursor++;
        }

        if (cursor > start)
        {
            into.Add(text[start..cursor]);
        }
    }

    private static bool Match(string text, int index, string token) =>
        index + token.Length <= text.Length &&
        string.CompareOrdinal(text, index, token, 0, token.Length) == 0;
}
