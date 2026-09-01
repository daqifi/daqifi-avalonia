// Ported by hand from upstream Daqifi.Desktop.Common/Loggers/AppLogErrorException.cs.
//
// DO NOT manually delete the `// @port:` markers — they link symbols back to
// the correspondence map.

namespace Daqifi.Desktop.Common.Loggers;

/// <summary>
/// Carrier exception for message-only errors reported through <see cref="AppLogger.Error(string)"/>.
/// Sentry requires an exception to capture an event; using a dedicated type (instead of a bare
/// <see cref="Exception"/>) keeps these events grouped separately from real thrown exceptions.
/// </summary>
/// <remarks>
/// The scaffold dropped this type and left <c>Error(string)</c> capturing
/// <c>new Exception(message)</c>. What that costs is the event's <b>type</b>, not its stack
/// trace: <c>AttachStacktrace</c> has defaulted to true since SDK 3.22.0 and applies to a
/// never-thrown exception too, so these events do arrive with the full caller chain and group
/// by call site (measured against SDK 6.8.0 — capturing a never-thrown exception four frames
/// deep produced all four frames, innermost <c>AppLogger.Error</c>).
/// <para>
/// The type is still worth fixing. <see cref="Exception"/> is the single most generic type in
/// .NET and the one a dependency uses for a bare <c>throw new Exception(...)</c>, so an issue
/// titled "Exception: ..." says nothing about where it came from. "AppLogErrorException: ..."
/// says exactly one thing — the app called <see cref="AppLogger.Error(string)"/> — which means
/// there is no thrown-exception detail to go looking for beyond the message, and the type can
/// be filtered and alerted on separately from real faults.
/// </para>
/// </remarks>
// @port: Daqifi.Desktop.Common.Loggers.AppLogErrorException
public class AppLogErrorException : Exception
{
    /// <summary>Creates the carrier exception with no message.</summary>
    // @port: Daqifi.Desktop.Common.Loggers.AppLogErrorException.AppLogErrorException
    public AppLogErrorException()
    {
    }

    /// <summary>Creates the carrier exception for the given logged message.</summary>
    /// <param name="message">The message that was logged as an error.</param>
    // @port: Daqifi.Desktop.Common.Loggers.AppLogErrorException.AppLogErrorException
    public AppLogErrorException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the carrier exception with an inner exception.</summary>
    /// <param name="message">The message that was logged as an error.</param>
    /// <param name="innerException">The originating exception.</param>
    // @port: Daqifi.Desktop.Common.Loggers.AppLogErrorException.AppLogErrorException
    public AppLogErrorException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
