using System.Reflection;
using Daqifi.Avalonia.Views;
using Daqifi.Core.Communication.Messages;
using Daqifi.Core.Device;
using Daqifi.Desktop.Device;
using Xunit;
using ConnectionType = Daqifi.Desktop.Device.ConnectionType;

namespace Daqifi.Avalonia.Tests.Views;

/// <summary>
/// Minimal concrete <see cref="AbstractStreamingDevice"/> over a real Core device, for tests that
/// drive <see cref="MobileShellViewModel"/>. The wrapper's own <c>SendMessage</c> is a no-op:
/// nothing here reads what the wrapper sends, and the silent transport these tests pair it with
/// would not answer it anyway.
/// </summary>
internal sealed class ShellTestDevice : AbstractStreamingDevice
{
    public ShellTestDevice(string name)
    {
        Name = name;
    }

    public override ConnectionType ConnectionType => ConnectionType.Wifi;

    protected override void SendMessage(IOutboundMessage<string> message)
    {
    }

    public void AttachCore(DaqifiStreamingDevice coreDevice) => CoreDevice = coreDevice;

    public void SyncFromCore(DaqifiDevice coreDevice) => SyncFromCoreDevice(coreDevice);

    /// <summary>
    /// Hands this device to the shell the way a completed connection does. The shell's adopt step
    /// is private — the public routes into it (WiFi connect, USB connect) each need a live
    /// transport or a platform connector — so it is reached by name, and the test exercises the
    /// shell's real adoption rather than a copy of it.
    /// </summary>
    public void AdoptInto(MobileShellViewModel shell)
    {
        var adopt = typeof(MobileShellViewModel).GetMethod(
            "AdoptConnectedDevice", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(adopt);
        adopt.Invoke(shell, [this]);
    }
}
