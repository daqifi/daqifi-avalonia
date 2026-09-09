using System.Xml.Linq;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Guards what makes <c>MainWindow.axaml</c>'s 32 bindings the XAML compiler's business rather than
/// nobody's (issue #327), and the two things compiled bindings still cannot see in this view.
///
/// <para>
/// The desktop shell declared neither <c>x:DataType</c> nor <c>x:CompileBindings</c>, so every
/// binding on it resolved by reflection at run time. Measured on <c>origin/main</c>, one deliberate
/// typo at a time across ten binding shapes — the <c>Window</c>'s own <c>Height</c>, a toolbar
/// toggle, the notification badge's converter binding, a <c>&lt;Binding Path=/&gt;</c> child of the
/// <c>SplitView</c>'s <c>MultiBinding</c>, both segments of an <c>AppSettings.*</c> path, the
/// settings scrim's <c>Command</c>, the tab strip's <c>SelectedIndex</c>, a flyout
/// <c>IsVisible</c> and the busy overlay's <c>FallbackValue</c> binding — every one built with
/// <c>0 Error(s)</c> and no diagnostic naming the file, while a bogus CLR property in the same tag
/// did error, which is what proves the file was being compiled at all. On this branch each of the
/// ten is <c>AVLN2000</c> against <c>DaqifiViewModel</c>.
/// </para>
///
/// <para>
/// Member names are therefore not asserted here — the compiler owns all 32 at once. This class
/// covers the remainder: the declarations themselves, whose removal is silent; the
/// <c>x:DataType</c> claim, which the compiler trusts without ever checking it against the
/// <c>DataContext</c> the window actually assigns; and the five tab <c>ContentControl</c>s, whose
/// <c>Content="{Binding}"</c> reads like noise and is not.
/// </para>
/// </summary>
public class MainWindowBindingTests
{
    private const string View = "Daqifi.Avalonia/Daqifi.Desktop/MainWindow.axaml";

    /// <summary>The XAML language namespace, where <c>x:DataType</c> and friends live.</summary>
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement Root() =>
        XDocument.Parse(BindingFacts.Source(View)).Root
        ?? throw new InvalidOperationException($"{View} has no root element.");

    /// <summary>
    /// Gap 1: nothing else in the build notices either declaration being dropped. Delete
    /// <c>x:CompileBindings</c> and all 32 bindings go back on reflection while desktop, iOS and
    /// every other test in this assembly stay green — <c>AvaloniaUseCompiledBindingsByDefault</c> is
    /// <c>false</c>, so <c>x:DataType</c> alone buys IntelliSense and nothing else (issue #326).
    ///
    /// <para>
    /// Asserted by <b>parsing</b> the root element rather than searching the file, and this view is
    /// its own counter-example to the substring form: the comment added beside the declarations
    /// spells out both literals several times, so an <c>Assert.Contains</c> over the raw markup
    /// passes with the real attributes deleted from the tag (Qodo round 2 on PR #325).
    /// </para>
    ///
    /// <para>
    /// Only the <c>CompileBindings</c> half is load-bearing. Measured: deleting
    /// <c>x:CompileBindings</c> builds green and this test is the only thing that objects, while
    /// deleting <c>x:DataType</c> is already an <c>AVLN2000</c> — <c>x:CompileBindings="True"</c>
    /// with no scope to compile against cannot go quiet. The <c>DataType</c> row is kept because it
    /// pins the <i>value</i>, not the presence, and the value is what the next test depends on.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("DataType", "vm:DaqifiViewModel")]
    [InlineData("CompileBindings", "True")]
    public void The_window_declares_its_data_type(string attribute, string value)
    {
        var actual = Root().Attribute(Xaml + attribute)?.Value;

        Assert.True(
            actual is not null,
            $"{View}: the root element declares no x:{attribute}. A copy of the literal elsewhere in "
            + "the file — the comment that explains it, say — does not enable compiled bindings.");
        Assert.Equal(value, actual);
    }

    /// <summary>
    /// Gap 2, and the one compiled bindings cannot close: <c>x:DataType</c> is a <i>claim</i>, and
    /// the compiler checks the 32 bindings against the claim rather than against the object the
    /// window will actually meet. Point it at a type that happens to expose the same member names
    /// and every binding still compiles while the shell renders blank — the failure #327 exists to
    /// remove, arriving one level up.
    ///
    /// <para>
    /// So this pins the claim to the <c>DataContext</c> the constructor assigns. The prefix the
    /// claim is written with is checked too: repointing <c>xmlns:vm</c> at another namespace would
    /// otherwise silently change what the scope means without touching the <c>x:DataType</c>.
    /// </para>
    ///
    /// <para>
    /// Read off the code-behind source rather than by constructing the window. Instantiating
    /// <c>MainWindow</c> constructs a <c>DaqifiViewModel</c>, which opens the application database,
    /// configuration and logs — under a test run that is the developer's real
    /// <c>~/Library/Application Support/DAQiFi</c>. The type name is spelled with <c>nameof</c>, so
    /// renaming the view model breaks this file's compile rather than leaving the assertion looking
    /// for a string that no longer exists.
    /// </para>
    /// </summary>
    [Fact]
    public void The_declared_scope_is_the_view_model_the_window_actually_assigns()
    {
        var root = Root();

        // Avalonia spells a CLR-namespace prefix either way; both are in use in this checkout.
        var prefix = root.Attribute(XNamespace.Xmlns + "vm")?.Value;
        Assert.True(
            prefix == $"using:{typeof(DaqifiViewModel).Namespace}"
            || prefix == $"clr-namespace:{typeof(DaqifiViewModel).Namespace}",
            $"{View}: xmlns:vm is {prefix ?? "absent"}, so the x:DataType below does not name "
            + $"{typeof(DaqifiViewModel).FullName}.");

        Assert.Equal($"vm:{nameof(DaqifiViewModel)}", root.Attribute(Xaml + "DataType")?.Value);

        BindingFacts.AssertBinds(
            "Daqifi.Avalonia/Daqifi.Desktop/MainWindow.axaml.cs",
            $"DataContext = new {nameof(DaqifiViewModel)}();");
    }

    /// <summary>
    /// Gap 3: compiled bindings type-check the <i>path</i>, never the target, so which property a
    /// binding feeds is still nobody's business. This pins the one coupling in the view where
    /// getting it wrong is invisible to both the compiler and a look at the screen.
    ///
    /// <para>
    /// The three flyouts are not independent slide-outs — they are stacked in the pane of a single
    /// <c>SplitView</c>, each shown by its own <c>IsVisible</c>, and the pane is opened by a
    /// <c>MultiBinding</c> OR-ing the same three view-model booleans. The two lists have to stay the
    /// same list. Drop one from the <c>MultiBinding</c> and that flyout becomes unreachable: its
    /// <c>IsVisible</c> goes true inside a pane that never opens. Add one and the pane slides out
    /// empty. Both compile, and both are states a "does the shell still look right?" pass sees only
    /// if it happens to open that flyout.
    /// </para>
    ///
    /// <para>
    /// Compared by set rather than in order — the OR is commutative, so the order genuinely does not
    /// matter and pinning it would only manufacture failures.
    /// </para>
    /// </summary>
    [Fact]
    public void The_pane_opens_for_exactly_the_flyouts_it_holds()
    {
        var root = Root();

        var paneOpen = root.Descendants()
            .Single(element => element.Name.LocalName == "SplitView.IsPaneOpen")
            .Descendants()
            .Where(element => element.Name.LocalName == "Binding")
            .Select(element => element.Attribute("Path")?.Value)
            .ToHashSet();

        var flyouts = root.Descendants()
            .Where(element => element.Name.LocalName.EndsWith("Flyout", StringComparison.Ordinal))
            .Select(element => element.Attribute("IsVisible")?.Value)
            .Select(value => value?.Replace("{Binding ", string.Empty).TrimEnd('}'))
            .ToHashSet();

        Assert.Equal(3, flyouts.Count);
        Assert.Equal(flyouts, paneOpen);
    }

    /// <summary>
    /// Gap 4: <c>Content="{Binding}"</c> on the five tab hosts, which reads like a no-op and is the
    /// only thing putting <c>DaqifiViewModel</c> into each pane's <c>DataContext</c>.
    ///
    /// <para>
    /// <c>LiveGraphPane</c> and <c>LoggedDataPanePrototype</c> have no view model of their own —
    /// <c>LoggedDataPanePrototype</c> reads <c>DataContext as DaqifiViewModel</c> directly — so
    /// deleting the bare binding does not fail a build or a compiled-binding check, it just renders
    /// those two tabs blank. The other three replace their own <c>DataContext</c> on attach and
    /// would survive, which is what makes the deletion look safe when someone tries it.
    /// </para>
    ///
    /// <para>
    /// It is also the markup that decides what a future <c>x:DataType</c> on these templates would
    /// have to say. The five <c>DataTemplate</c>s deliberately declare none, and there is no test
    /// demanding one, because measured on Avalonia 12.1 a <c>ContentControl.ContentTemplate</c>
    /// infers <b>no</b> scope: a binding inside one resolves against
    /// <c>XamlX.TypeSystem.XamlPseudoType</c> and is <c>AVLN2000</c> even with a correct member name.
    /// That holds here even though <c>Content="{Binding}"</c> hands the compiler the
    /// <c>DataContext</c> type statically, and with <c>Content="{Binding AppSettings}"</c> too — the
    /// inference does not follow the <c>Content</c> binding at all. So a template here that binds
    /// something already fails the build, and a test asserting the same thing could never fail;
    /// worse, it would fire wrongly on a <c>ListBox.ItemTemplate</c> added later, which <i>does</i>
    /// infer its item type and correctly needs no declaration. Whoever adds the first binding to one
    /// of these five wants <c>x:DataType="vm:DaqifiViewModel"</c> and the comment in the view says so.
    /// </para>
    /// </summary>
    [Fact]
    public void Each_tab_host_passes_the_view_model_down_to_its_pane()
    {
        var hosts = Root().Descendants()
            .Where(element => element.Name.LocalName == "ContentControl")
            .Where(element => element.Elements().Any(
                child => child.Name.LocalName == "ContentControl.ContentTemplate"))
            .ToList();

        Assert.Equal(5, hosts.Count);
        Assert.All(hosts, host => Assert.Equal("{Binding}", host.Attribute("Content")?.Value));
    }

    /// <summary>
    /// Gap 5: the two escape hatches that would let this view read as protected while checking
    /// nothing — <c>x:CompileBindings="False"</c> on a subtree, and <c>{ReflectionBinding}</c> on an
    /// individual binding. Either one puts part of the file back on reflection with the root
    /// declaration still in place and the build still green, so the count in the PR body and the
    /// guard above would both be measuring less than they claim.
    ///
    /// <para>
    /// There are none today; this is here so that adding one has to be deliberate and explained
    /// rather than quietly absorbed. If a binding genuinely cannot be expressed, narrow the opt-out
    /// to that binding and say why in the markup — then update this test to allow exactly it.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("x:CompileBindings=\"False\"")]
    [InlineData("ReflectionBinding")]
    public void The_view_opens_no_escape_hatch(string hatch) =>
        Assert.DoesNotContain(hatch, BindingFacts.Source(View), StringComparison.Ordinal);
}
