namespace Daqifi.Desktop.Services;

// System.Windows' MessageBox enums have no Avalonia counterpart (hal:
// message_box). Member names and numeric values match WPF so ported call
// sites stay source-compatible after a using swap.

public enum MessageBoxButton
{
    OK = 0,
    OKCancel = 1,
    YesNoCancel = 3,
    YesNo = 4,
}

public enum MessageBoxImage
{
    None = 0,
    Error = 16,
    Question = 32,
    Warning = 48,
    Information = 64,
}

public enum MessageBoxResult
{
    None = 0,
    OK = 1,
    Cancel = 2,
    Yes = 6,
    No = 7,
}
