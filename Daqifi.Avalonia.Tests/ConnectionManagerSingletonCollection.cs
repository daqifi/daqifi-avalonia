using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// Serialises the test classes that write to the <c>ConnectionManager</c> singleton.
///
/// <para>
/// <c>ConnectionManager.Instance</c> is process-wide and <c>DeviceBeingUpdated</c> is a plain mutable
/// field on it, which the connection dialog reads as "a firmware update is running" — the gate that
/// stops its discovery from opening COM ports and broadcasting UDP. xUnit runs distinct test classes
/// in parallel by default, so without a shared collection the two classes that drive that flag
/// interleave inside one process: one clears the flag while the other is relying on it to hold
/// discovery down, and the assertion that no finder was created instead meets a real one.
/// </para>
///
/// <para>
/// That failure is rare, never reproduces on demand, and reaches real hardware on a developer's
/// machine when it does — the worst possible shape for a CI signal, so the fix is to stop the
/// interleaving rather than to retry it. Membership costs these classes their parallelism against
/// each other and nothing else; the rest of the suite still runs in parallel around them.
/// </para>
///
/// <para>
/// Only classes that touch <c>ConnectionManager.Instance</c> belong here. Classes that construct
/// their own <c>ConnectionManager</c> (such as <c>ConnectionManagerTeardownTests</c>) share no state
/// and must stay outside it.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConnectionManagerSingletonCollection
{
    public const string Name = "ConnectionManager singleton";
}
