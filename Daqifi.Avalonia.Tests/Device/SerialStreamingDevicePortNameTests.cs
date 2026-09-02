using System.IO.Ports;
using Daqifi.Desktop.Device.SerialDevice;
using Xunit;

namespace Daqifi.Avalonia.Tests.Device;

/// <summary>
/// Pins <see cref="SerialStreamingDevice.PortName"/> as the projection the connect dialog's USB
/// tile binds.
///
/// That tile used to bind <c>Port.PortName</c> — walking from the view into the
/// <see cref="SerialPort"/> the device owns. The bindings in that file are not compile-checked
/// (no <c>x:DataType</c>), so nothing but a test holds the replacement in place: a rename or a
/// visibility change on this property renders the tile's port line blank at runtime while every
/// head still builds green.
/// </summary>
public class SerialStreamingDevicePortNameTests
{
    /// <summary>
    /// A port name and a device name that cannot be confused for each other — Core's discovery
    /// sets <c>Name</c> to the part number once a device answers, which is why the tile needs a
    /// separate port line at all.
    /// </summary>
    private const string PortName = "COM9";

    [Fact]
    public void PortName_reports_the_port_even_when_the_device_has_been_named()
    {
        var device = new SerialStreamingDevice(PortName, "Nq1", "SN-TEST", "3.7.2");

        Assert.Equal("Nq1", device.Name);
        Assert.Equal(PortName, device.PortName);
    }

    [Fact]
    public void PortName_falls_back_to_the_device_name_when_there_is_no_port()
    {
        // The fallback the old Port.PortName binding did not have: it rendered blank instead.
        var device = new SerialStreamingDevice(PortName, "Nq1", "SN-TEST", "3.7.2") { Port = null };

        Assert.Equal("Nq1", device.PortName);
    }

    [Fact]
    public void Replacing_the_port_notifies_the_bound_property()
    {
        // The old binding got its refresh from the Port notification and the path walk. A binding
        // on PortName only refreshes if PortName itself is raised.
        var device = new SerialStreamingDevice(PortName, "Nq1", "SN-TEST", "3.7.2");
        var raised = new List<string?>();
        device.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        device.Port = new SerialPort("COM12");

        Assert.Contains(nameof(SerialStreamingDevice.PortName), raised);
        Assert.Equal("COM12", device.PortName);
    }
}
