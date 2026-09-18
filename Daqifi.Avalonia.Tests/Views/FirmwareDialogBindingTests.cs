using System.Xml.Linq;
using Daqifi.Desktop.Models;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Guards what makes <c>FirmwareDialog.axaml</c>'s 14 bindings the XAML compiler's business rather
/// than nobody's (issue #327), and the things compiled bindings still cannot see in this dialog.
///
/// <para>
/// The dialog declared neither <c>x:DataType</c> nor <c>x:CompileBindings</c>, and
/// <c>Daqifi.Avalonia.csproj</c> sets <c>AvaloniaUseCompiledBindingsByDefault</c> to <c>false</c>, so
/// every binding on it resolved by reflection at run time. Measured on <c>origin/main</c> in a
/// <b>single</b> build carrying two defects at once: a bogus CLR property on the progress bar was
/// <c>AVLN2000</c> at line 61 of this file — which proves the file was compiled — while
/// <c>{Binding UploadFirmwareProgressTextZZY}</c>, a member that does not exist, in the same file and
/// the same compilation, produced no diagnostic at all.
/// </para>
///
/// <para>
/// On this branch five deliberately wrong bindings, each applied <b>alone</b> to a fresh edit, are each a single
/// <c>AVLN2000</c>: a negated <c>IsVisible</c>, a <c>Command</c>, and a <c>TwoWay</c>
/// <c>SelectedItem</c> against <c>FirmwareDialogViewModel</c>; and in the <c>ComboBox</c>'s
/// <c>DisplayMemberBinding</c> both a misspelled item member and a correctly spelled
/// <b>view-model</b> member (<c>HasErrorOccured</c>) against <c>Daqifi.Desktop.Models.FirmwareOption</c>
/// — the second showing that scope is inferred from the <c>ItemsSource</c> beside it and does not
/// fall back to the view model. Member names are therefore not asserted here.
/// </para>
/// </summary>
public class FirmwareDialogBindingTests
{
    private const string View = "Daqifi.Avalonia/Daqifi.Desktop/View/FirmwareDialog.axaml";

    private static XElement Root() =>
        XDocument.Parse(BindingFacts.Source(View)).Root
        ?? throw new InvalidOperationException($"{View} has no root element.");

    /// <summary>
    /// Gap 1: nothing else in the build notices either declaration being dropped. Deleting
    /// <c>x:CompileBindings</c> puts all 14 bindings back on reflection while both heads and every
    /// other test stay green. Parsed, not searched: the comment beside the declarations spells out
    /// both literals, so a substring search would pass with the attributes gone from the tag.
    /// </summary>
    [Theory]
    [InlineData("DataType", "vm:FirmwareDialogViewModel")]
    [InlineData("CompileBindings", "True")]
    public void The_dialog_declares_its_data_type(string attribute, string value) =>
        BindingFacts.AssertRootDeclares(View, attribute, value);

    /// <summary>
    /// Gap 2: <c>x:DataType</c> is a claim, and this dialog is handed its view model as an
    /// <c>object</c> by <c>ShowDialogAsync&lt;FirmwareDialog&gt;</c> in <c>ConnectionDialogViewModel</c>.
    /// See <see cref="BindingFacts.AssertDialogIsHandedItsDeclaredViewModel"/>.
    /// </summary>
    [Fact]
    public void The_declared_scope_is_the_view_model_the_dialog_is_actually_handed() =>
        BindingFacts.AssertDialogIsHandedItsDeclaredViewModel(View, typeof(FirmwareDialogViewModel));

    /// <summary>
    /// Gap 3: the <c>ComboBox</c>'s <c>DisplayMemberBinding</c> takes its scope from the element type
    /// of <c>AvailableFirmwares</c>, which the markup never names. Changing what that collection holds
    /// silently re-scopes the binding, and is a build error only if the new type happens not to carry
    /// a <c>Display</c> member — so the element type is pinned here rather than by an
    /// <c>x:DataType</c>, which a binding assigned to a property has no element to carry.
    /// </summary>
    [Fact]
    public void The_dropdown_label_is_scoped_to_the_firmware_option_it_renders()
    {
        var property = typeof(FirmwareDialogViewModel).GetProperty(nameof(FirmwareDialogViewModel.AvailableFirmwares))!;
        var itemType = property.PropertyType.GetInterfaces()
            .Concat([property.PropertyType])
            .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
            .Select(i => i.GetGenericArguments()[0])
            .Distinct()
            .Single();
        Assert.Equal(typeof(FirmwareOption), itemType);

        var combo = Root().Descendants().Single(element => element.Name.LocalName == "ComboBox");
        Assert.Equal($"{{Binding {nameof(FirmwareDialogViewModel.AvailableFirmwares)}}}", combo.Attribute("ItemsSource")?.Value);
        Assert.Equal($"{{Binding {nameof(FirmwareOption.Display)}}}", combo.Attribute("DisplayMemberBinding")?.Value);
    }

    /// <summary>
    /// Gap 4: compiled bindings type-check the <i>path</i>, never which property it feeds or whether
    /// it is negated. The dialog is two full-size panels in one <c>Grid</c> cell — before and after
    /// upload — and only <c>IsVisible</c> keeps one of them off the screen. A binding that does not
    /// resolve leaves <c>IsVisible</c> at its default <c>true</c>, so a lost gate does not hide a
    /// panel, it draws it over the other; and swapping the two gates, or dropping a <c>!</c>, compiles
    /// perfectly because the path is the same member either way.
    ///
    /// <para>
    /// Each panel is identified by what it <b>contains</b> — the firmware picker, or the
    /// <c>Upload Complete</c> line — rather than by position or as a set: a set of the two gates is
    /// unchanged by swapping them, which is exactly the mistake worth catching. The count check keeps
    /// an ungated third panel from slipping past.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("ComboBox", null, false)]
    [InlineData("TextBlock", "Upload Complete", true)]
    public void The_before_and_after_panels_are_gated_by_opposite_sides_of_one_flag(
        string marker, string? text, bool shownWhenComplete)
    {
        var layout = Root().Elements().Single(element => element.Name.LocalName == "Grid");
        var panels = layout.Elements().ToList();

        Assert.True(
            panels.Count == 2 && panels.All(panel => panel.Attribute("IsVisible") is not null),
            $"{View}: the layout root holds {panels.Count} panels, "
            + $"{panels.Count(panel => panel.Attribute("IsVisible") is null)} of them ungated; expected "
            + "exactly the two upload states. They share one Grid cell, so an ungated panel draws over "
            + "the other.");

        var panel = panels.Single(candidate => candidate.Descendants().Any(
            element => element.Name.LocalName == marker
                       && (text is null || element.Attribute("Text")?.Value == text)));

        var expected = shownWhenComplete
            ? $"{{Binding {nameof(FirmwareDialogViewModel.IsUploadComplete)}}}"
            : $"{{Binding !{nameof(FirmwareDialogViewModel.IsUploadComplete)}}}";
        Assert.Equal(expected, panel.Attribute("IsVisible")?.Value);
    }

    /// <summary>
    /// Gap 4, continued, on the upload scrim (issue #241). The scrim covers every control in the
    /// before-upload panel, including the dialog's own Cancel, and its <b>Cancel Upload</b> button is
    /// the only way out of a stalled flash short of killing the app. The button's command's
    /// <c>CanExecute</c> is <c>IsFirmwareUploading</c>, so the scrim that holds it must be gated by
    /// that same flag: gated by anything else, it is either up with the button disabled — no way out —
    /// or it is <c>true</c> by default and covers the idle dialog. The button is found by its
    /// automation id rather than by position.
    /// </summary>
    [Fact]
    public void The_cancel_upload_button_lives_on_the_scrim_raised_by_the_upload_flag()
    {
        var button = Root().Descendants()
            .Single(element => element.Attributes()
                .Any(attribute => attribute.Name.LocalName == "AutomationProperties.AutomationId"
                                  && attribute.Value == "FirmwareCancelUpload"));

        Assert.Equal(
            $"{{Binding {nameof(FirmwareDialogViewModel.CancelUploadFirmwareCommand)}}}",
            button.Attribute("Command")?.Value);
        Assert.Equal(
            $"{{Binding {nameof(FirmwareDialogViewModel.IsFirmwareUploading)}}}",
            button.Parent?.Attribute("IsVisible")?.Value);
    }

    /// <summary>
    /// Gap 4, last: the error line is shown when an error <b>has</b> occurred. Negating it compiles,
    /// and leaves "something went wrong" on screen for every healthy dialog.
    /// </summary>
    [Fact]
    public void The_error_line_follows_the_error_flag()
    {
        var line = Root().Descendants()
            .Single(element => element.Name.LocalName == "TextBlock"
                               && (element.Attribute("Text")?.Value ?? "").StartsWith("Sorry, something went wrong", StringComparison.Ordinal));

        Assert.Equal($"{{Binding {nameof(FirmwareDialogViewModel.HasErrorOccured)}}}", line.Attribute("IsVisible")?.Value);
    }

    /// <summary>
    /// Gap 5: the three escape hatches — <c>x:CompileBindings</c> set to anything but <c>True</c> on a
    /// subtree, <c>{ReflectionBinding}</c>, and the <c>&lt;ReflectionBinding/&gt;</c> object element —
    /// any of which puts markup back on reflection with the root declaration in place and the build
    /// green. None exist in this file today.
    /// </summary>
    [Fact]
    public void The_view_opens_no_escape_hatch() => BindingFacts.AssertNoEscapeHatch(View);
}
