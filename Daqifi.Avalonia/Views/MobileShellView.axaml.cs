using System.Reflection;
using Avalonia.Controls;

namespace Daqifi.Avalonia.Views;

public partial class MobileShellView : UserControl
{
    public MobileShellView()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "dev";
        // informational version carries "+<full-sha>" — keep it phone-width
        var plus = version.IndexOf('+');
        var sha = plus >= 0 && version.Length > plus + 8
            ? $" ({version.Substring(plus + 1, 7)})" : "";
        VersionText.Text = $"v{(plus >= 0 ? version[..plus] : version)}{sha}";
    }
}
