using Avalonia.Controls;
using Avalonia.Interactivity;
using Daqifi.Desktop.Services;
using Projektanker.Icons.Avalonia;

namespace Daqifi.Desktop.View.Dialogs;

/// <summary>
/// Owned replacement for WPF's MessageBox (hal: message_box). Only
/// DialogService/IMessageBoxService construct it — the dialog_service
/// mechanism owns every dialog path.
/// </summary>
public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
    }

    public MessageDialog(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon)
        : this()
    {
        Title = caption;
        this.FindControl<TextBlock>("MessageText")!.Text = messageBoxText;

        var glyph = this.FindControl<Icon>("IconGlyph")!;
        glyph.IsVisible = icon != MessageBoxImage.None;
        glyph.Value = icon switch
        {
            MessageBoxImage.Error => "mdi-alert-circle",
            MessageBoxImage.Warning => "mdi-alert",
            MessageBoxImage.Question => "mdi-help-circle-outline",
            _ => "mdi-information-outline",
        };

        this.FindControl<Button>("OkButton")!.IsVisible =
            button is MessageBoxButton.OK or MessageBoxButton.OKCancel;
        this.FindControl<Button>("CancelButton")!.IsVisible =
            button is MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel;
        this.FindControl<Button>("YesButton")!.IsVisible =
            button is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel;
        this.FindControl<Button>("NoButton")!.IsVisible =
            button is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(MessageBoxResult.OK);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(MessageBoxResult.Cancel);

    private void OnYes(object? sender, RoutedEventArgs e) => Close(MessageBoxResult.Yes);

    private void OnNo(object? sender, RoutedEventArgs e) => Close(MessageBoxResult.No);
}
