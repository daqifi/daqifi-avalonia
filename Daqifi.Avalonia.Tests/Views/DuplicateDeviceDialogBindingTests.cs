using System.Xml.Linq;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Guards what makes <c>DuplicateDeviceDialog.axaml</c>'s 3 bindings the XAML compiler's business
/// rather than nobody's (issue #327), and the two things compiled bindings still cannot see in this
/// dialog.
///
/// <para>
/// The dialog declared neither <c>x:DataType</c> nor <c>x:CompileBindings</c>, and
/// <c>Daqifi.Avalonia.csproj</c> sets <c>AvaloniaUseCompiledBindingsByDefault</c> to <c>false</c>, so
/// all three bindings resolved by reflection at run time. Measured on <c>origin/main</c> in a
/// <b>single</b> build carrying two defects at once: a bogus CLR property on the message
/// <c>TextBlock</c> was <c>AVLN2000 … ZzzBogusProperty on type … TextBlock</c> naming this file and
/// its line — which proves the file was compiled — while <c>{Binding MessageQQQ}</c>, a member that
/// does not exist, on the same element in the same compilation produced no diagnostic at all. The
/// same one-build pair on this branch is two <c>AVLN2000</c>s, the second naming
/// <c>Daqifi.Desktop.ViewModels.DuplicateDeviceDialogViewModel</c>; and a typo in each of the two
/// <c>RadioButton</c> labels, each applied alone, is one <c>AVLN2000</c> against that type. Member
/// names are therefore not asserted here: the compiler owns all three.
/// </para>
///
/// <para>
/// The view contains no <c>DataTemplate</c>, so it has exactly one binding scope.
/// </para>
/// </summary>
public class DuplicateDeviceDialogBindingTests
{
    private const string View = "Daqifi.Avalonia/Daqifi.Desktop/View/DuplicateDeviceDialog.axaml";

    /// <summary>The XAML language namespace, where <c>x:DataType</c> and friends live.</summary>
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static XElement Root() =>
        XDocument.Parse(BindingFacts.Source(View)).Root
        ?? throw new InvalidOperationException($"{View} has no root element.");

    /// <summary>
    /// Gap 1: nothing else in the build notices either declaration being dropped. Delete
    /// <c>x:CompileBindings</c> and the three bindings go back on reflection with every head and every
    /// other test still green — <c>x:DataType</c> alone changes nothing while
    /// <c>AvaloniaUseCompiledBindingsByDefault</c> is <c>false</c> (#326). Parsed, not searched: the
    /// comment in the view spells both attributes out.
    /// </summary>
    [Theory]
    [InlineData("DataType", "vm:DuplicateDeviceDialogViewModel")]
    [InlineData("CompileBindings", "True")]
    public void The_dialog_declares_its_data_type(string attribute, string value) =>
        BindingFacts.AssertRootDeclares(View, attribute, value);

    /// <summary>
    /// Gap 2, the one compiled bindings cannot close: <c>x:DataType</c> is a <i>claim</i>, checked
    /// against itself rather than against the object the dialog will meet. The dialog is constructed
    /// directly and handed its <c>DataContext</c> by assignment — typed <c>object</c> — so a type sharing
    /// these three member names would compile and render a dialog with no message and two unlabelled
    /// choices. See <see cref="BindingFacts.AssertDialogIsHandedItsDeclaredViewModel"/>.
    /// </summary>
    [Fact]
    public void The_declared_scope_is_the_view_model_the_dialog_is_actually_handed() =>
        BindingFacts.AssertDialogIsHandedItsDeclaredViewModel(View, typeof(DuplicateDeviceDialogViewModel));

    /// <summary>
    /// Gap 3: compiled bindings type-check the <i>path</i>, never which control it feeds. The two
    /// choices are told apart only by name: <c>BtnOk_Click</c> reads <c>SwitchToNewRadio.IsChecked</c>
    /// and treats anything else as keep-existing. Swap the two <c>Content</c> bindings — or the two
    /// <c>x:Name</c>s — and everything still compiles, while the button labelled "Switch to USB" keeps
    /// the existing connection and the one labelled "Keep WiFi (recommended)" drops it. That is the
    /// opposite of what the user chose, on a dialog whose whole job is that one choice.
    ///
    /// <para>
    /// The recommended option is the one pre-checked, so pressing OK without reading keeps the
    /// connection the user already has. Pinned alongside, because moving <c>IsChecked</c> to the other
    /// button is the same swap made one attribute over.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("KeepExistingRadio", nameof(DuplicateDeviceDialogViewModel.KeepExistingText), "True")]
    [InlineData("SwitchToNewRadio", nameof(DuplicateDeviceDialogViewModel.SwitchToNewText), null)]
    public void Each_choice_is_labelled_with_what_OK_will_do_with_it(
        string radioName, string labelMember, string? isChecked)
    {
        var radios = Root().Descendants().Where(element => element.Name.LocalName == "RadioButton").ToList();
        Assert.Equal(2, radios.Count);

        var radio = radios.Single(element => element.Attribute(Xaml + "Name")?.Value == radioName);

        Assert.Equal($"{{Binding {labelMember}}}", radio.Attribute("Content")?.Value);
        Assert.Equal(isChecked, radio.Attribute("IsChecked")?.Value);
    }

    /// <summary>
    /// Gap 4: the escape hatches that would put markup back on reflection with the root declaration
    /// still in place. See <see cref="BindingFacts.AssertNoEscapeHatch"/>.
    /// </summary>
    [Fact]
    public void The_view_opens_no_escape_hatch() => BindingFacts.AssertNoEscapeHatch(View);
}
