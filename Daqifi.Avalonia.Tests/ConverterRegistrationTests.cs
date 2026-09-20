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
            .Where(r => !markup.ReferencedKeys.Contains(r.Key))
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
        Assert.True(markup.ReferencedKeys.Count >= 6, $"Only {markup.ReferencedKeys.Count} resource keys were referenced by a binding.");
    }

    private sealed record Registration(string Key, string TypeName, string File);

    private sealed record Markup(int FileCount, IReadOnlyList<Registration> Registrations, IReadOnlySet<string> ReferencedKeys);

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

                    CollectResourceKeys(attribute.Value, referenced);
                }
            }
        }

        return new Markup(fileCount, registrations, referenced);
    }

    /// <summary>
    /// Pulls every <c>{StaticResource Key}</c> / <c>{DynamicResource Key}</c> out of one attribute
    /// value. Both the braced markup-extension form and the bare form that appears inside a nested
    /// extension (<c>{Binding Converter={StaticResource Key}}</c>) are matched, because the nested
    /// one is how almost every converter in this repo is actually reached.
    /// </summary>
    private static void CollectResourceKeys(string attributeValue, HashSet<string> into)
    {
        const string staticResource = "StaticResource";
        const string dynamicResource = "DynamicResource";

        for (var index = 0; index < attributeValue.Length; index++)
        {
            var length = Match(attributeValue, index, staticResource) ? staticResource.Length
                : Match(attributeValue, index, dynamicResource) ? dynamicResource.Length
                : 0;
            if (length == 0)
            {
                continue;
            }

            var cursor = index + length;
            while (cursor < attributeValue.Length && attributeValue[cursor] == ' ')
            {
                cursor++;
            }

            var start = cursor;
            while (cursor < attributeValue.Length &&
                   (char.IsLetterOrDigit(attributeValue[cursor]) || attributeValue[cursor] is '_' or '.'))
            {
                cursor++;
            }

            if (cursor > start)
            {
                into.Add(attributeValue[start..cursor]);
            }

            index = cursor - 1;
        }
    }

    private static bool Match(string text, int index, string token) =>
        index + token.Length <= text.Length &&
        string.CompareOrdinal(text, index, token, 0, token.Length) == 0;
}
