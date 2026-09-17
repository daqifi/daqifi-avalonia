using System.Text.RegularExpressions;
using System.Xml.Linq;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Guards what makes <c>ExportDialog.axaml</c>'s 22 bindings the XAML compiler's business rather than
/// nobody's (issue #327), and the three things compiled bindings still cannot see in this dialog.
///
/// <para>
/// The dialog declared neither <c>x:DataType</c> nor <c>x:CompileBindings</c>, and
/// <c>Daqifi.Avalonia.csproj</c> sets <c>AvaloniaUseCompiledBindingsByDefault</c> to <c>false</c>, so
/// every binding on it resolved by reflection at run time. Measured on <c>origin/main</c> in a
/// <b>single</b> build carrying two defects at once: a bogus CLR property on the progress bar was
/// <c>AVLN2000 … ZzzBogusPropertyControl on … ProgressBar</c> naming this file and its line — which is
/// what proves the file was compiled at all — while <c>{Binding}</c> at a member that does not exist,
/// in the same file and the same compilation, produced no diagnostic whatsoever. One build, one
/// failure, one silence, so neither half can be a stale incremental result of the other.
/// </para>
///
/// <para>
/// On this branch four deliberate typos, each applied <b>alone</b> and covering the four binding
/// shapes in the file — a plain <c>Text</c> path, a negated <c>IsVisible</c>, a <c>Command</c>, and a
/// path carrying the <c>StringFormat={}</c> escape — are each a single <c>AVLN2000</c> against
/// <c>Daqifi.Desktop.ViewModels.ExportDialogViewModel</c>. Member names are therefore not asserted
/// here: the compiler owns all 22 at once.
/// </para>
///
/// <para>
/// The view contains <b>no</b> <c>DataTemplate</c>, so it has exactly one binding scope and there is
/// nothing to declare per-template, nor any template scope for this class to pin.
/// </para>
/// </summary>
public class ExportDialogBindingTests
{
    private const string View = "Daqifi.Avalonia/Daqifi.Desktop/View/ExportDialog.axaml";
    private const string Host = "Daqifi.Avalonia/Daqifi.Desktop/ViewModels/DaqifiViewModel.cs";

    /// <summary>The XAML language namespace, where <c>x:DataType</c> and friends live.</summary>
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement Root() =>
        XDocument.Parse(BindingFacts.Source(View)).Root
        ?? throw new InvalidOperationException($"{View} has no root element.");

    /// <summary>
    /// Gap 1: nothing else in the build notices either declaration being dropped. Delete
    /// <c>x:CompileBindings</c> and all 22 bindings go back on reflection while desktop, iOS and every
    /// other test in this assembly stay green — <c>x:DataType</c> alone buys IntelliSense and nothing
    /// else when <c>AvaloniaUseCompiledBindingsByDefault</c> is <c>false</c>, which is the trap #326
    /// was filed for.
    ///
    /// <para>
    /// Asserted by <b>parsing</b> the root element rather than by searching the file, and this view is
    /// its own counter-example to the substring form: the comment added beside the declarations spells
    /// out both literals, so an <c>Assert.Contains</c> over the raw markup passes with the real
    /// attributes deleted from the tag (Qodo round 2 on PR #325).
    /// </para>
    ///
    /// <para>
    /// Only the <c>CompileBindings</c> half is load-bearing on its own: deleting <c>x:DataType</c> is
    /// already an <c>AVLN2000</c>, because <c>x:CompileBindings="True"</c> with no scope to compile
    /// against cannot go quiet. The <c>DataType</c> row is here because it pins the <i>value</i>, not
    /// the presence, and the value is what the next test depends on.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("DataType", "vm:ExportDialogViewModel")]
    [InlineData("CompileBindings", "True")]
    public void The_dialog_declares_its_data_type(string attribute, string value)
    {
        var actual = Root().Attribute(Xaml + attribute)?.Value;

        Assert.True(
            actual is not null,
            $"{View}: the root element declares no x:{attribute}. A copy of the literal elsewhere in "
            + "the file — the comment that explains it, say — does not enable compiled bindings.");
        Assert.Equal(value, actual);
    }

    /// <summary>
    /// Gap 2, and the one compiled bindings cannot close: <c>x:DataType</c> is a <i>claim</i>, and the
    /// compiler checks the 22 bindings against the claim rather than against the object the dialog
    /// will actually meet. Point it at a type that happens to expose the same member names and every
    /// binding still compiles while the dialog renders blank — the failure #327 exists to remove,
    /// arriving one level up.
    ///
    /// <para>
    /// Nothing in the type system closes it here, which is why this is asserted rather than assumed:
    /// the dialog has no <c>DataContext</c> of its own, and is handed one by
    /// <c>IDialogService.ShowDialogAsync&lt;T&gt;(object ownerViewModel, object viewModel)</c> — whose
    /// view-model parameter is typed <c>object</c>. Both call sites could hand <c>ExportDialog</c> any
    /// object at all and still compile.
    /// </para>
    ///
    /// <para>
    /// So this reads the call sites out of the host's source and requires each to pass a local that
    /// was constructed as an <c>ExportDialogViewModel</c>. Read off the source rather than by
    /// constructing anything: <c>ExportDialogViewModel</c>'s public constructors resolve
    /// <c>App.ServiceProvider</c>, and <c>DaqifiViewModel</c> opens the application database,
    /// configuration and logs — under a test run that is the developer's real
    /// <c>~/Library/Application Support/DAQiFi</c>. The type name is spelled with <c>nameof</c>, so
    /// renaming the view model breaks this file's compile rather than leaving the assertion hunting a
    /// string that no longer exists.
    /// </para>
    /// </summary>
    [Fact]
    public void The_declared_scope_is_the_view_model_the_dialog_is_actually_handed()
    {
        var root = Root();

        // Avalonia spells a CLR-namespace prefix either way; both are in use in this checkout.
        var prefix = root.Attribute(XNamespace.Xmlns + "vm")?.Value;
        Assert.True(
            prefix == $"using:{typeof(ExportDialogViewModel).Namespace}"
            || prefix == $"clr-namespace:{typeof(ExportDialogViewModel).Namespace}",
            $"{View}: xmlns:vm is {prefix ?? "absent"}, so the x:DataType below does not name "
            + $"{typeof(ExportDialogViewModel).FullName}.");

        Assert.Equal($"vm:{nameof(ExportDialogViewModel)}", root.Attribute(Xaml + "DataType")?.Value);

        var host = BindingFacts.Source(Host);

        var callSites = Regex.Matches(
            host, @"ShowDialogAsync<ExportDialog>\(\s*[A-Za-z_][A-Za-z0-9_]*\s*,\s*(?<arg>[A-Za-z_][A-Za-z0-9_]*)\s*\)");

        Assert.True(
            callSites.Count > 0,
            $"{Host}: no ShowDialogAsync<ExportDialog>(owner, viewModel) call site found. If the "
            + "dialog is now presented some other way, this guard has to follow it — the x:DataType "
            + "claim is unchecked until something pins it to the object the dialog is given.");

        Assert.All(callSites, site =>
        {
            var local = site.Groups["arg"].Value;
            var constructed = Regex.IsMatch(
                host, $@"\b(var|{nameof(ExportDialogViewModel)})\s+{Regex.Escape(local)}\s*=\s*new\s+{nameof(ExportDialogViewModel)}\s*\(");

            Assert.True(
                constructed,
                $"{Host}: ShowDialogAsync<ExportDialog> is handed '{local}', which is not constructed "
                + $"as a {nameof(ExportDialogViewModel)} in this file. That parameter is typed object, "
                + $"so the compiler accepts anything, and {View} claims "
                + $"x:DataType=\"vm:{nameof(ExportDialogViewModel)}\" — the dialog would compile and "
                + "render blank.");
        });
    }

    /// <summary>
    /// Gap 3: compiled bindings type-check the <i>path</i>, never the target, so which property a
    /// binding feeds is still nobody's business. This pins the coupling in the dialog where getting it
    /// wrong is invisible to the compiler and nearly invisible on screen.
    ///
    /// <para>
    /// The dialog is three full-size panels stacked in one <c>Grid</c> cell — configure, exporting,
    /// result — and only <c>IsVisible</c> keeps two of the three off the screen. They are mutually
    /// exclusive in the view model (<c>IsConfiguring</c> is <c>!IsExporting &amp;&amp;
    /// !IsExportComplete</c>), so the markup is the <i>only</i> place that can get this wrong, and the
    /// failure direction is the bad one: a binding that does not resolve leaves its target at the
    /// property default, and <c>IsVisible</c> defaults to <c>true</c>. A panel that loses its gate
    /// does not disappear — it draws on top of the one that should be showing.
    /// </para>
    ///
    /// <para>
    /// Collected as the gated children of the layout root and compared as a set: the three are
    /// siblings in one cell and their order in the markup is not what selects them.
    /// </para>
    /// </summary>
    [Fact]
    public void Each_of_the_three_states_is_gated_by_its_own_flag()
    {
        var layout = Root().Elements().Single(element => element.Name.LocalName == "Grid");

        var gates = layout.Elements()
            .Select(element => element.Attribute("IsVisible")?.Value)
            .Where(value => value is not null)
            .ToList();

        Assert.True(
            gates.Count == layout.Elements().Count(),
            $"{View}: {layout.Elements().Count() - gates.Count} of the layout root's "
            + $"{layout.Elements().Count()} panels carry no IsVisible. All of them share one Grid "
            + "cell, and an ungated panel draws over whichever state is supposed to be on screen.");

        Assert.Equal(
            new HashSet<string>
            {
                $"{{Binding {nameof(ExportDialogViewModel.IsConfiguring)}}}",
                $"{{Binding {nameof(ExportDialogViewModel.IsExporting)}}}",
                $"{{Binding {nameof(ExportDialogViewModel.IsExportComplete)}}}",
            },
            gates.ToHashSet()!);
    }

    /// <summary>
    /// Gap 3, continued, on the fork inside the result panel. Success and failure share one panel and
    /// differ only by which controls are shown: a green tick and <b>Open Folder</b>, or a red alert
    /// and <b>Try Again</b>. Every one of those four is gated by <c>ExportSucceeded</c> or its
    /// negation, and swapping a pair compiles perfectly — the path is the same member either way.
    ///
    /// <para>
    /// That is not cosmetic. <b>Open Folder</b> on a failed export opens the folder the export could
    /// not write, and offering <b>Try Again</b> after a successful one invites a second export over
    /// the first. Issue #312's whole subject was this dialog claiming success it had not earned; this
    /// keeps the claim and the affordance pointing the same way.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("Open Folder", false)]
    [InlineData("Try Again", true)]
    public void The_result_buttons_follow_the_result(string content, bool shownOnFailure)
    {
        var button = Root().Descendants()
            .Single(element => element.Name.LocalName == "Button"
                               && element.Attribute("Content")?.Value == content);

        var expected = shownOnFailure
            ? $"{{Binding !{nameof(ExportDialogViewModel.ExportSucceeded)}}}"
            : $"{{Binding {nameof(ExportDialogViewModel.ExportSucceeded)}}}";

        Assert.Equal(expected, button.Attribute("IsVisible")?.Value);
    }

    /// <summary>
    /// And the same fork on the two icons, keyed by the glyph rather than by document order, so
    /// reordering the panel cannot quietly satisfy this.
    /// </summary>
    [Theory]
    [InlineData("mdi-check-circle-outline", false)]
    [InlineData("mdi-alert-circle-outline", true)]
    public void The_result_icons_follow_the_result(string glyph, bool shownOnFailure)
    {
        var icon = Root().Descendants()
            .Single(element => element.Name.LocalName == "Icon"
                               && element.Attribute("Value")?.Value == glyph);

        var expected = shownOnFailure
            ? $"{{Binding !{nameof(ExportDialogViewModel.ExportSucceeded)}}}"
            : $"{{Binding {nameof(ExportDialogViewModel.ExportSucceeded)}}}";

        Assert.Equal(expected, icon.Attribute("IsVisible")?.Value);
    }

    /// <summary>
    /// Gap 4: the two escape hatches that would let this view read as protected while checking
    /// nothing — <c>x:CompileBindings="False"</c> on a subtree, and <c>{ReflectionBinding}</c> on an
    /// individual binding. Either one puts part of the file back on reflection with the root
    /// declaration still in place and the build still green, so the count in the PR body and the guard
    /// above would both be measuring less than they claim.
    ///
    /// <para>
    /// There are none today; this is here so that adding one has to be deliberate and explained rather
    /// than quietly absorbed. If a binding genuinely cannot be expressed, narrow the opt-out to that
    /// binding and say why in the markup — then update this test to allow exactly it.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("x:CompileBindings=\"False\"")]
    [InlineData("ReflectionBinding")]
    public void The_view_opens_no_escape_hatch(string hatch) =>
        Assert.DoesNotContain(hatch, BindingFacts.Source(View), StringComparison.Ordinal);
}
