using System.Collections;
using System.Xml.Linq;
using Daqifi.Avalonia.Views;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Guards the three declarations that make <c>MobileShellView.axaml</c>'s 29 bindings the XAML
/// compiler's business instead of nobody's (issue #326).
///
/// <para>
/// The view declared <c>x:DataType</c> from the day it was written and never declared
/// <c>x:CompileBindings</c>. <c>Daqifi.Avalonia.csproj</c> sets
/// <c>AvaloniaUseCompiledBindingsByDefault</c> to <c>false</c>, so the <c>x:DataType</c> bought
/// IntelliSense and nothing else: every binding still resolved by reflection at run time. Measured on
/// <c>origin/main</c> — the manual-IP box repointed at a member that does not exist built with
/// <c>0 Error(s)</c> and <c>175 Warning(s)</c>, identical to a clean build, and no diagnostic named
/// it. That is worse than declaring neither, because the declaration reads to a maintainer as if
/// something were checking.
/// </para>
///
/// <para>
/// With <c>x:CompileBindings="True"</c> that same typo is <c>AVLN2000</c>. So the member names in
/// this view are no longer asserted here — the compiler owns them, for all 29 bindings at once
/// rather than a hand-picked few, and inside each <c>DataTemplate</c> against the item type rather
/// than the view model. What is left for this class is the three things the compiler still cannot
/// see, one test each.
/// </para>
/// </summary>
public class MobileShellViewBindingTests
{
    private const string View = "Daqifi.Avalonia/Views/MobileShellView.axaml";

    /// <summary>The XAML language namespace, where <c>x:DataType</c> and friends live.</summary>
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement Root() =>
        XDocument.Parse(BindingFacts.Source(View)).Root
        ?? throw new InvalidOperationException($"{View} has no root element.");

    private static IEnumerable<XElement> Templates(XElement root) =>
        root.Descendants().Where(element => element.Name.LocalName == "DataTemplate");

    /// <summary>
    /// Gap 1: nothing else in the build notices either declaration being dropped. Deleting
    /// <c>x:CompileBindings</c> puts all 29 bindings back on reflection while desktop, iOS and every
    /// other test in this assembly stay green — so this is the only thing that would object.
    ///
    /// <para>
    /// Asserted by <b>parsing</b> the root element, never by searching the file. The substring form
    /// of this guard does not hold here and the counter-example is already in the checkout: the
    /// explanatory comment inside this very view spells out <c>x:CompileBindings</c> three times, so
    /// an <c>Assert.Contains</c> over the raw markup passes with the real attribute deleted from the
    /// tag. XML gives no way to comment an attribute out in place, but nothing stops the literal
    /// living elsewhere in a file that discusses it at length. (Same lesson as Qodo round 2 on
    /// PR #325; this class is off that PR's branch, so the helper is restated here rather than
    /// shared, to keep the two diffs from colliding.)
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("DataType", "views:MobileShellViewModel")]
    [InlineData("CompileBindings", "True")]
    public void The_view_declares_its_data_type(string attribute, string value)
    {
        var actual = Root().Attribute(Xaml + attribute)?.Value;

        Assert.True(
            actual is not null,
            $"{View}: the root element declares no x:{attribute}. A copy of the literal elsewhere in "
            + "the file — the comment that explains it, say — does not enable compiled bindings.");
        Assert.Equal(value, actual);
    }

    /// <summary>
    /// Gap 2: which item type each <c>DataTemplate</c> is scoped to. Inside a template the
    /// <c>DataContext</c> is the item, not the view model.
    ///
    /// <para>
    /// A template that loses its <c>x:DataType</c> does <b>not</b> fall back to the inherited scope —
    /// that wording was wrong and is corrected here (issue #336). Measured on this view, on Avalonia
    /// 12.1, with both declarations deleted: correct member names still build with <c>0 Error(s)</c>;
    /// <c>{Binding IsSelectedZZZ}</c> in the channel template is <c>AVLN2000 … on type
    /// 'Daqifi.Avalonia.Views.ChannelToggle'</c>, the same type the deleted attribute named; and
    /// <c>{Binding ShowDeviceList}</c> — a <c>MobileShellViewModel</c> member — is <c>AVLN2000</c>
    /// against <c>ChannelToggle</c> too, not a silent success and not a resolution against the view
    /// model. Both lists here are <c>ItemsControl.ItemTemplate</c> over a bound <c>ItemsSource</c>,
    /// the shape that infers its item type, so these two declarations are redundant rather than
    /// load-bearing. They are kept because they are correct and churning them would reset a settled
    /// review; what the attribute does buy is in the next gap. See
    /// <see cref="BindingFacts.AssertTemplateScopedTo"/> for the full rule and the shapes that infer
    /// nothing.
    /// </para>
    ///
    /// <para>
    /// Keyed by the <c>ItemsSource</c> the owning list is bound to rather than by looking for each
    /// type somewhere in the file, because a set of scopes cannot tell two lists apart: exchanging
    /// the two <c>x:DataType</c>s leaves the set identical and would satisfy an any-of assertion
    /// while the channel grid rendered device tiles. Neither list carries an <c>x:Name</c>, and the
    /// binding it renders is a better key than a name added only to be asserted.
    /// </para>
    ///
    /// <para>
    /// The compiler does reject that particular swap today — measured, <c>AVLN2000</c> — but only
    /// because <c>ChannelToggle</c> and <c>MobileDeviceItem</c> happen to expose different members.
    /// That is a fact about the model right now, not a property of compiled bindings, and it stops
    /// holding the moment the two converge on the names this view binds.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("{Binding Channels}", "views:ChannelToggle")]
    [InlineData("{Binding Devices}", "views:MobileDeviceItem")]
    public void Each_item_template_declares_the_scope_of_the_list_it_renders(
        string itemsSource, string expectedItemType)
    {
        var root = Root();

        // The every-template half, which a per-list check cannot cover: a fixed list of the two
        // templates that exist today would not notice a third added later. An undeclared template
        // resolves against a scope inferred from the ItemsSource beside it, or — in a shape that
        // infers nothing, such as a ContentControl.ContentTemplate — against XamlPseudoType, which
        // is a build error. Never against an inherited scope (issue #336).
        var all = Templates(root).ToList();
        var unscoped = all.Count(template => template.Attribute(Xaml + "DataType") is null);
        Assert.True(
            unscoped == 0,
            $"{View}: {unscoped} of {all.Count} DataTemplates declare no x:DataType, so their "
            + "bindings resolve against a scope inferred from the surrounding ItemsSource, or "
            + "against nothing at all where the shape supports no such inference.");

        var list = root.Descendants()
            .SingleOrDefault(element => element.Attribute("ItemsSource")?.Value == itemsSource);
        Assert.True(list is not null, $"{View}: no element sets ItemsSource=\"{itemsSource}\".");

        var scoped = Templates(list!)
            .Select(template => template.Attribute(Xaml + "DataType")?.Value)
            .ToList();
        Assert.True(
            scoped.Count == 1,
            $"{View}: the list bound to {itemsSource} holds {scoped.Count} DataTemplates, expected one.");
        Assert.Equal(expectedItemType, scoped[0]);
    }

    /// <summary>
    /// Gap 3, and the one compiled bindings genuinely cannot close: an <c>x:DataType</c> is a
    /// <i>claim</i> about what the list holds, and the compiler checks the members against the claim
    /// rather than against the collection. Declare the wrong item type and every binding in the
    /// template still compiles — against a type that is never in the list — and the template renders
    /// blank at run time, which is the exact failure mode #326 is about, moved one level up.
    ///
    /// <para>
    /// So this pins the claim to the collection's real element type. The markup names the type in
    /// XAML's <c>prefix:Name</c> form, and the prefix maps to the CLR namespace via the
    /// <c>xmlns:views</c> declaration on the root, which is asserted here too — repointing that at
    /// another namespace would otherwise silently change what every scope in the file means.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(nameof(MobileShellViewModel.Channels), typeof(ChannelToggle))]
    [InlineData(nameof(MobileShellViewModel.Devices), typeof(MobileDeviceItem))]
    public void The_declared_item_scope_is_what_the_collection_actually_holds(
        string collectionName, Type expectedItemType)
    {
        var root = Root();

        // The prefix the scopes are written with has to resolve to the namespace these types live in.
        Assert.Equal(
            $"clr-namespace:{expectedItemType.Namespace}",
            root.Attribute(XNamespace.Xmlns + "views")?.Value);

        var declared = root.Descendants()
            .Where(element => element.Attribute("ItemsSource")?.Value == $"{{Binding {collectionName}}}")
            .SelectMany(Templates)
            .Select(template => template.Attribute(Xaml + "DataType")?.Value)
            .Single();
        Assert.Equal($"views:{expectedItemType.Name}", declared);

        // ... and the view model's collection has to actually be a collection of that type.
        var property = typeof(MobileShellViewModel).GetProperty(collectionName);
        Assert.NotNull(property);
        Assert.True(
            typeof(IEnumerable).IsAssignableFrom(property!.PropertyType),
            $"MobileShellViewModel.{collectionName} is not enumerable, so no template renders it.");

        var elementType = property.PropertyType.GetInterfaces()
            .Concat([property.PropertyType])
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(i => i.GetGenericArguments()[0])
            .Distinct()
            .Single();
        Assert.Equal(expectedItemType, elementType);
    }

    /// <summary>
    /// Gap 4: which <b>attribute</b> a binding feeds. Compiled bindings type-check the path, not the
    /// target — moved from <c>Text</c> to <c>Tag</c>, or swapped between two <c>IsVisible</c>s, the
    /// markup compiles just as happily and the screen is wrong.
    ///
    /// <para>
    /// The bindings pinned here are the ones where that is not cosmetic. The manual-IP path is the
    /// mobile head's only route onto a network with AP client isolation, and it is three bindings
    /// wide. The visibility gates are worse than they look: a binding that fails leaves its target at
    /// the property <b>default</b>, and <c>IsVisible</c> defaults to <c>true</c> — so the failure
    /// shows a pane that should be hidden rather than hiding one that should show, which no "does the
    /// shell still look right?" pass can see. Eleven of the 29 bindings in this view feed
    /// <c>IsVisible</c>; these four are the ones that gate a whole section.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Text=\"{Binding ManualIp}\"")]
    [InlineData("Text=\"{Binding ManualPort}\"")]
    [InlineData("Command=\"{Binding ManualConnectCommand}\"")]
    [InlineData("IsVisible=\"{Binding ShowChannelSelector}\"")]
    [InlineData("IsVisible=\"{Binding ShowDeviceList}\"")]
    [InlineData("IsVisible=\"{Binding IsUsbAvailable}\"")]
    [InlineData("IsVisible=\"{Binding IsConnected}\"")]
    public void The_bindings_feed_the_attributes_they_are_supposed_to(string binding) =>
        BindingFacts.AssertBinds(View, binding);
}
