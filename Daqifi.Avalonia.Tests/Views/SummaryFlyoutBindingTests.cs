using System.Xml.Linq;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Guards the two attributes that make <c>SummaryFlyout.axaml</c>'s 29 bindings the XAML compiler's
/// business instead of nobody's, and the facts those two attributes lean on (part of issue #327).
///
/// <para>
/// The view declared neither <c>x:DataType</c> nor <c>x:CompileBindings</c>, and
/// <c>Daqifi.Avalonia.csproj</c> sets <c>AvaloniaUseCompiledBindingsByDefault</c> to <c>false</c>, so
/// every binding in it resolved by reflection at run time. Measured on the parent commit
/// (<c>74308c5</c>): one deliberate typo in each of the view's three binding scopes, each applied
/// alone, built with <c>0 Error(s)</c> and not one diagnostic naming the file. Each of those three is
/// now <c>AVLN2000</c> naming the type its own scope resolves against —
/// <c>DaqifiViewModel</c>, <c>SummaryLogger/DeviceSummary</c>, <c>SummaryLogger/ChannelSummary</c>.
/// </para>
///
/// <para>
/// So the member names are not asserted here: the compiler owns them now, for all 29 bindings rather
/// than a hand-picked few. What is left for this class is the part the compiler cannot see.
/// </para>
///
/// <para>
/// Two hazards it deliberately does <b>not</b> test, because the build now rejects them outright.
/// Lifting a <c>DataTemplate</c> out of its list into <c>UserControl.Resources</c> gives it no usage
/// site to take a scope from: measured on this file, a resource template binding <c>Name</c> is
/// <c>AVLN2000 … on type 'XamlX.TypeSystem.XamlPseudoType'</c>, even with the root
/// <c>x:DataType</c> in place. Unbinding a list's <c>ItemsSource</c> is the same kind of loud. Both
/// are compile errors, so a test asserting them would only restate the build.
/// </para>
///
/// <para>
/// Every assertion below <b>parses</b> the markup; none of them searches it. This view's own prose
/// discusses the export dialog's number box and the flyout's sections by name, and a whole-file
/// <c>Assert.Contains</c> can be satisfied by a comment while the attribute itself is gone from the
/// tag — which is how PR #325's substring guard was defeated in Qodo round 2 there.
/// </para>
/// </summary>
public class SummaryFlyoutBindingTests
{
    private const string View = "Daqifi.Avalonia/Daqifi.Desktop/View/Flyouts/SummaryFlyout.axaml";

    /// <summary>The XAML language namespace, where <c>x:DataType</c> and friends live.</summary>
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement Root() =>
        XDocument.Parse(BindingFacts.Source(View)).Root
        ?? throw new InvalidOperationException($"{View} has no root element.");

    /// <summary>The single element of the given type, which is how the controls here are keyed.</summary>
    private static XElement TheOnly(string elementName)
    {
        var matches = Root().Descendants()
            .Where(element => element.Name.LocalName == elementName)
            .ToList();

        Assert.True(
            matches.Count == 1,
            $"{View}: {matches.Count} <{elementName}> elements, expected exactly 1 — the bindings below "
            + "are keyed by element type, so a second one makes them ambiguous rather than wrong.");
        return matches[0];
    }

    /// <summary>The element carrying the given automation id — the ids the UI harness already uses.</summary>
    private static XElement ElementWithAutomationId(string automationId)
    {
        var matches = Root().Descendants()
            .Where(element => element.Attribute("AutomationProperties.AutomationId")?.Value == automationId)
            .ToList();

        Assert.True(
            matches.Count == 1,
            $"{View}: {matches.Count} elements carry AutomationProperties.AutomationId=\"{automationId}\", expected 1.");
        return matches[0];
    }

    /// <summary>The element type of a collection, as a binding would see its items.</summary>
    private static Type ItemTypeOf(Type owner, string collectionName)
    {
        var property = owner.GetProperty(collectionName);
        Assert.True(property is not null, $"{owner.Name} has no public {collectionName} property.");

        return property!.PropertyType.GetInterfaces()
            .Concat([property.PropertyType])
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(i => i.GetGenericArguments()[0])
            .Distinct()
            .Single();
    }

    /// <summary>The nearest <c>IsVisible</c> at or above an element — the gate that actually shows it.</summary>
    private static string GateOver(XElement element)
    {
        var gated = element.AncestorsAndSelf()
            .FirstOrDefault(candidate => candidate.Attribute("IsVisible") is not null);

        Assert.True(
            gated is not null,
            $"{View}: nothing from <{element.Name.LocalName}> up to the root sets IsVisible, so this "
            + "half of the pane is always showing.");
        return gated!.Attribute("IsVisible")!.Value;
    }

    /// <summary>
    /// Gap 1: nothing else in the build notices either declaration being dropped. Deleting
    /// <c>x:CompileBindings</c> puts all 29 bindings back on reflection while both heads and every
    /// other test in this assembly stay green — so this is the only thing that would object.
    ///
    /// <para>
    /// The flyout has no <c>DataContext</c> of its own: <c>MainWindow.axaml</c> hosts it in the shared
    /// <c>SplitView</c> pane, so it meets whatever the window is bound to. That is what makes the
    /// declared type a claim worth pinning rather than a restatement of the file.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("DataType", "vm:DaqifiViewModel")]
    [InlineData("CompileBindings", "True")]
    public void The_view_declares_its_data_type(string attribute, string value) =>
        BindingFacts.AssertRootDeclares(View, attribute, value);

    /// <summary>
    /// Gap 2, and the fact this view leans on hardest: neither of its two <c>DataTemplate</c>s declares
    /// an <c>x:DataType</c>. Both scopes are <b>inferred</b> from the collection beside them, and the
    /// inner one is inferred through the outer — measured, a typo in the channel rows is
    /// <c>AVLN2000 … on type 'Daqifi.Desktop.Logger.SummaryLogger/ChannelSummary'</c>, two levels down
    /// from the root declaration, with no <c>DataType</c> written anywhere in the file.
    ///
    /// <para>
    /// Declaring them anyway would be worse than leaving them off. An explicit <c>x:DataType</c>
    /// overrides the inference rather than confirming it, so a plausible-but-wrong one replaces a
    /// correct scope with a wrong one and only fails if the two types happen not to share member names
    /// (issue #327, second comment).
    /// </para>
    ///
    /// <para>
    /// The consequence is what this test pins. Because the scopes are inferred, the ELEMENT TYPES of
    /// these two collections are load-bearing markup that the markup never names: changing what
    /// <c>SummaryLogger.Devices</c> holds silently re-scopes every device section, and changing what
    /// <c>DeviceSummary.Channels</c> holds silently re-scopes all eleven per-channel figures. Under a
    /// declared <c>x:DataType</c> that would be a build error; under inference it is an error only if
    /// the new type happens not to carry the same member names.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("{Binding SummaryLogger.Devices}")]
    [InlineData("{Binding Channels}")]
    public void The_inferred_item_scopes_come_from_the_collections_beside_them(string itemsSource)
    {
        Assert.Equal(typeof(SummaryLogger.DeviceSummary), ItemTypeOf(typeof(SummaryLogger), nameof(SummaryLogger.Devices)));
        Assert.Equal(
            typeof(SummaryLogger.ChannelSummary),
            ItemTypeOf(typeof(SummaryLogger.DeviceSummary), nameof(SummaryLogger.DeviceSummary.Channels)));

        // One parse, because the ownership check below compares element identity.
        var root = Root();

        var lists = root.Descendants()
            .Where(element => element.Attribute("ItemsSource")?.Value == itemsSource)
            .ToList();

        Assert.True(
            lists.Count == 1,
            $"{View}: {lists.Count} elements bind ItemsSource to {itemsSource}, expected 1 — the "
            + "DataTemplate inside it takes its item type from that collection and from nothing else.");

        // The nearest enclosing list is the one a template takes its scope from, which is why the
        // outer list's own descendants are not enough: the channel template is inside both of them.
        var owned = root.Descendants()
            .Where(element => element.Name.LocalName == "DataTemplate")
            .Where(template => template.Ancestors()
                .FirstOrDefault(ancestor => ancestor.Attribute("ItemsSource") is not null) == lists[0])
            .ToList();

        Assert.Single(owned);
    }

    /// <summary>
    /// The other half of the same fact, stated where a reader of the view will look for it: neither
    /// template declares a scope. This is not a style rule — it is the difference between the
    /// compiler's inference and an override of it, so a declaration appearing here later should be
    /// deliberate and measured (rule 4 in #327: a collection wider than what the template binds).
    /// </summary>
    [Fact]
    public void Neither_data_template_overrides_the_inferred_scope()
    {
        var declared = Root().Descendants()
            .Where(element => element.Name.LocalName == "DataTemplate")
            .Select(template => template.Attribute(Xaml + "DataType")?.Value)
            .Where(value => value is not null)
            .ToList();

        Assert.Empty(declared);
    }

    /// <summary>
    /// Gap 3: which <b>attribute</b> a binding feeds and which <b>element</b> it sits on. Compiled
    /// bindings type-check the path, not the target — moved between two sibling panels the markup
    /// compiles just as happily and the pane is wrong.
    ///
    /// <para>
    /// These two halves are the whole pane and exactly one of them is meant to be showing. A failed
    /// <c>bool</c> binding does not blank its control, it leaves the property at its <b>default</b>,
    /// and <c>IsVisible</c> defaults to <c>true</c> — which is how "NO DEVICES REPORTING" would come
    /// to sit on top of live per-channel figures. The compiler now catches a typo in either gate;
    /// what it cannot catch is the two gates being swapped, or the <c>!</c> being dropped from one of
    /// them, which is what pinning both sides together does.
    /// </para>
    /// </summary>
    [Fact]
    public void The_empty_state_and_the_device_list_are_gated_on_opposite_sides_of_the_same_member()
    {
        Assert.Equal(
            "{Binding !SummaryLogger.HasDevices}",
            GateOver(ElementWithAutomationId("SummaryEmptyTitle")));

        var deviceList = Root().Descendants()
            .Single(element => element.Attribute("ItemsSource")?.Value == "{Binding SummaryLogger.Devices}");
        Assert.Equal("{Binding SummaryLogger.HasDevices}", GateOver(deviceList));
    }

    /// <summary>
    /// Gap 3 for the settings box. <c>Mode=</c> is the part of a binding the compiler does not check,
    /// and both modes here are load-bearing.
    ///
    /// <para>
    /// The close button writes <c>IsLogSummaryOpen</c> back, so it must be <c>TwoWay</c> — as
    /// <c>OneWay</c> it would compile, render, and never close the flyout. The status switch is the
    /// opposite: it reports <c>Enabled</c> <c>OneWay</c> and does its work through
    /// <c>ToggleEnabledCommand</c>, because a <c>TwoWay</c> <c>IsChecked</c> beside that command would
    /// both write the property and run the toggle that inverts it.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("ToggleButton", "IsChecked", "{Binding IsLogSummaryOpen, Mode=TwoWay}")]
    [InlineData("ToggleSwitch", "IsChecked", "{Binding SummaryLogger.Enabled, Mode=OneWay}")]
    [InlineData("ToggleSwitch", "Command", "{Binding SummaryLogger.ToggleEnabledCommand}")]
    [InlineData("Button", "Command", "{Binding SummaryLogger.ResetCommand}")]
    [InlineData("NumericUpDown", "Value", "{Binding SummaryLogger.SampleSize}")]
    public void The_bindings_feed_the_attributes_they_are_supposed_to(
        string elementName, string attribute, string binding) =>
        Assert.Equal(binding, TheOnly(elementName).Attribute(attribute)?.Value);

    /// <summary>
    /// The headline: how many bindings this view actually has, counted by parsing rather than by
    /// searching for <c>{Binding</c>. Every one of them is now compile-checked; before this change
    /// none of them was.
    ///
    /// <para>
    /// A tripwire, not a specification. Adding a binding here is fine — but it lands in one of three
    /// scopes and two of those are inferred, so the number is the prompt to say which.
    /// </para>
    /// </summary>
    [Fact]
    public void All_twenty_nine_bindings_are_compile_checked()
    {
        var bindings = Root().DescendantsAndSelf()
            .SelectMany(element => element.Attributes())
            .Count(attribute => attribute.Value.TrimStart().StartsWith("{Binding", StringComparison.Ordinal));

        Assert.Equal(29, bindings);
    }

    /// <summary>
    /// The hole the two tests above leave between them: <c>x:CompileBindings</c> is <b>inherited and
    /// overridable per subtree</b>, and neither of them looks below the root.
    /// <see cref="The_view_declares_its_data_type"/> reads the root attribute only, and
    /// <see cref="All_twenty_nine_bindings_are_compile_checked"/> counts binding text, which an
    /// opted-out binding still contributes to. Found by Qodo on PR #335 and measured there:
    /// <c>x:CompileBindings="False"</c> on a subtree plus a binding to a member that does not exist is
    /// <c>0 Error(s)</c> with every test green — the same defect this PR removes, one layer up.
    ///
    /// <para>
    /// This carried its own copy of the check until issue #374 made <see cref="BindingFacts.AssertNoEscapeHatch"/>
    /// the single implementation, and the copy had the gap that helper closes: it scanned attribute
    /// values for <c>{ReflectionBinding</c> but not the OBJECT-ELEMENT spelling. Measured on this view:
    /// <c>&lt;TextBlock.Text&gt;&lt;ReflectionBinding Path="NoSuchMemberZZZ370"/&gt;&lt;/TextBlock.Text&gt;</c>
    /// in the header built with zero <c>AVLN2000</c> while all 13 tests in this class passed; the same
    /// path as <c>{Binding}</c> is <c>AVLN2000</c>, so the member really was dead and the file really
    /// was compiled. Through the helper the same mutation fails this test.
    /// </para>
    ///
    /// <para>
    /// #327 does contemplate switching checking off <i>narrowly</i>, for a binding that is genuinely
    /// inexpressible. This is not a veto on that; it is the requirement that doing so be deliberate,
    /// which means updating the helper to allow exactly that binding and saying why.
    /// </para>
    /// </summary>
    [Fact]
    public void The_view_opens_no_escape_hatch() => BindingFacts.AssertNoEscapeHatch(View);
}
