using System.IO.Ports;
using System.Reflection;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Pins the USB tab's manual-port pre-check: a port name the system does not enumerate is refused
/// with an inline message before any connect is attempted, and a name it does enumerate passes the
/// check whatever its casing.
///
/// <para>
/// The answer is "does the operating system's serial-port enumeration list this name", compared
/// case-insensitively. The view model asks Core for that list rather than enumerating serial ports
/// itself, so these tests compare against <see cref="SerialPort.GetPortNames"/> directly — the
/// enumeration the check is required to agree with, whichever layer performs it.
/// </para>
///
/// <para>
/// The positive leg can only exercise the ports this machine actually has; on a host that enumerates
/// none, only the negative leg runs (xUnit 2 has no dynamic skip to report that).
/// </para>
/// </summary>
[Collection(ConnectionManagerSingletonCollection.Name)]
public class ConnectionDialogManualPortCheckTests
{
    private const string AbsentPort = "DAQIFI-TEST-NO-SUCH-PORT";

    [Fact]
    public async Task A_port_the_system_does_not_enumerate_is_refused_before_any_connect()
    {
        Assert.DoesNotContain(AbsentPort, SerialPort.GetPortNames(), StringComparer.OrdinalIgnoreCase);

        var viewModel = CreateViewModel();
        try
        {
            var closeRequested = false;
            viewModel.CloseRequested += (_, _) => closeRequested = true;
            viewModel.ManualPortName = $"  {AbsentPort}  ";

            await viewModel.ConnectManualSerialCommand.ExecuteAsync(null);

            Assert.False(closeRequested, "A refused port must leave the dialog open.");
            Assert.Null(viewModel.ManualSerialDevice);
            Assert.Equal(
                $"Port '{AbsentPort}' is not available. " +
                "Plug in the device or check Device Manager for the correct port name.",
                viewModel.ManualPortError);
        }
        finally
        {
            viewModel.Close();
        }
    }

    [Fact]
    public void The_check_agrees_with_the_system_enumeration_case_insensitively()
    {
        Assert.False(IsPortAvailable(AbsentPort));

        foreach (var port in SerialPort.GetPortNames())
        {
            Assert.True(IsPortAvailable(port), $"'{port}' is enumerated and must pass.");
            Assert.True(IsPortAvailable(port.ToUpperInvariant()), $"'{port}' upper-cased must pass.");
            Assert.True(IsPortAvailable(port.ToLowerInvariant()), $"'{port}' lower-cased must pass.");
        }
    }

    private static bool IsPortAvailable(string portName)
    {
        var method = typeof(ConnectionDialogViewModel).GetMethod(
            "IsPortAvailable", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);
        return (bool)method.Invoke(null, [portName])!;
    }

    /// <summary>
    /// A view model with no bootloader watcher and its UI marshal replaced by a direct call — outside
    /// a running Avalonia app <c>Dispatcher.UIThread</c> is never pumped.
    /// </summary>
    private static ConnectionDialogViewModel CreateViewModel()
    {
        var viewModel = new ConnectionDialogViewModel(null!, null);
        var field = typeof(ConnectionDialogViewModel).GetField(
            "_marshalToUiThread", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(viewModel, (Action<Action>)(action => action()));
        return viewModel;
    }
}
