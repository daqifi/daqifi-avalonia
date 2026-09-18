using Daqifi.Desktop.Common.Loggers;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// The suite's one <see cref="IAppLogger"/> double: records the message of every Information,
/// Warning and Error call (the exception overloads record their message too) and ignores
/// breadcrumbs, device context and shutdown. A test that only needs a logger to exist can ignore
/// what it records. Thread-safe, because some code under test logs from a consumer thread.
/// </summary>
internal sealed class RecordingAppLogger : IAppLogger
{
    private readonly Lock _gate = new();
    private readonly List<string> _informations = [];
    private readonly List<string> _warnings = [];
    private readonly List<string> _errors = [];

    internal IReadOnlyList<string> Informations => Snapshot(_informations);

    internal IReadOnlyList<string> Warnings => Snapshot(_warnings);

    internal IReadOnlyList<string> Errors => Snapshot(_errors);

    public void Information(string message) => Record(_informations, message);

    public void Warning(string message) => Record(_warnings, message);

    public void Warning(Exception ex, string message) => Record(_warnings, message);

    public void Error(string message) => Record(_errors, message);

    public void Error(Exception ex, string message) => Record(_errors, message);

    public void AddBreadcrumb(string category, string message, BreadcrumbLevel level = BreadcrumbLevel.Info) { }

    public void SetDeviceContext(string model, string serialNumber, string firmwareVersion, string connectionType, int activeChannels) { }

    public void ClearDeviceContext() { }

    public void Shutdown() { }

    private void Record(List<string> list, string message)
    {
        lock (_gate) { list.Add(message); }
    }

    private IReadOnlyList<string> Snapshot(List<string> list)
    {
        lock (_gate) { return [.. list]; }
    }
}
