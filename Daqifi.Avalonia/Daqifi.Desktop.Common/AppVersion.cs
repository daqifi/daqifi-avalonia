// Downstream-only: there is no upstream Daqifi.Desktop.Common counterpart.
// Recorded in .portomatic/map/Common.yaml (downstream_only / downstream_only_files).
//
// Upstream is a single WPF head, so `Assembly.GetEntryAssembly()` there is always the app
// itself. This port has three heads and that call does NOT survive the crossing — see the
// remarks below.

using System.Reflection;

namespace Daqifi.Desktop.Common;

/// <summary>
/// The one place the running application's own version is resolved.
/// </summary>
/// <remarks>
/// <para>
/// Read from <see cref="AssemblyInformationalVersionAttribute"/> on <b>this</b> assembly — the
/// shared <c>Daqifi.Avalonia</c> library that every head links in, and the only assembly in the
/// solution that carries the app's version on all three heads.
/// </para>
/// <para>
/// The obvious alternative, <c>Assembly.GetEntryAssembly()?.GetName().Version</c>, asks a
/// question whose answer is a property of the head rather than of the app, and it gave a
/// different answer on each of the three:
/// </para>
/// <list type="bullet">
///   <item><description>
///     <b>Android</b> — <b>broken</b>, and this is #126. There is no managed entry assembly:
///     the process is started by the Android runtime through JNI rather than by a managed
///     <c>Main</c>, so <c>GetEntryAssembly()</c> returns <see langword="null"/> and the call
///     falls through to whatever literal follows the <c>??</c>. A real event captured on
///     hardware came through tagged <c>release: 0.0.0</c>.
///   </description></item>
///   <item><description>
///     <b>iOS and desktop</b> — a number, but not the app's. Both heads do have a managed
///     entry point, and both currently yield <c>3.3.0.0</c>: the Desktop head declares
///     <c>&lt;Version&gt;</c>, and the iOS head does not but the Apple SDK derives it from
///     <c>&lt;ApplicationDisplayVersion&gt;</c> (verified: the csproj sets no
///     <c>&lt;Version&gt;</c>, MSBuild evaluates <c>Version=3.3.0</c>). So the release read
///     <c>3.3.0.0</c> — four parts where the store lists three, and no commit, all resting on
///     an implicit SDK mapping and on which assembly happens to be the entry point.
///   </description></item>
/// </list>
/// <para>
/// Reading the shared assembly makes the answer the same on every head and independent of how
/// the head was started.
/// </para>
/// <para>
/// Every value here is nullable rather than defaulted to a plausible-looking literal. A
/// placeholder such as <c>"0.0.0"</c> is indistinguishable downstream from a real version, so
/// callers get to decide: the Sentry initialisation leaves <c>Release</c> unset (letting the
/// SDK apply its own detection), while the mobile shell shows <c>"dev"</c>.
/// </para>
/// </remarks>
public static class AppVersion
{
    /// <summary>
    /// The full informational version, including any <c>+&lt;build-metadata&gt;</c> suffix —
    /// e.g. <c>3.3.0+9f1c2ab…</c>. <see langword="null"/> when the version cannot be resolved.
    /// </summary>
    /// <remarks>
    /// This is the string used as the Sentry release. The suffix is the git commit, appended by
    /// the SDK from <c>SourceRevisionId</c> (SourceLink is on by default), and it is wanted
    /// there: it makes each build its own release and maps a crash to an exact commit, which
    /// matters on mobile where a store version and a build are not 1:1. A build without source
    /// control information simply has no suffix.
    /// </remarks>
    public static string? Informational { get; } = ResolveInformational();

    /// <summary>
    /// The version with any <c>+&lt;build-metadata&gt;</c> suffix removed — e.g. <c>3.3.0</c>.
    /// This is the version a user sees, and the one the app stores advertise.
    /// <see langword="null"/> when the version cannot be resolved.
    /// </summary>
    public static string? Semantic { get; } = TrimBuildMetadata(Informational);

    /// <summary>
    /// The <c>+&lt;build-metadata&gt;</c> suffix truncated to its first 7 characters — the short
    /// commit sha for a normal build. <see langword="null"/> when the build carries no metadata.
    /// </summary>
    public static string? ShortBuildMetadata { get; } = ResolveShortBuildMetadata(Informational);

    private static string? ResolveInformational()
    {
        var assembly = typeof(AppVersion).Assembly;

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational)) { return informational; }

        // Fallback for the case where the attribute is absent — the assembly version is
        // metadata rather than an attribute, so nothing can strip it. 0.0.0.0 is what an
        // assembly with no declared version gets, and reporting that would recreate the very
        // bug this type exists to fix, so treat it as "unknown" instead.
        var assemblyVersion = assembly.GetName().Version;
        return assemblyVersion is null || assemblyVersion == new Version(0, 0, 0, 0)
            ? null
            : assemblyVersion.ToString();
    }

    private static string? TrimBuildMetadata(string? informational)
    {
        if (informational is null) { return null; }

        var plus = informational.IndexOf('+');
        return plus >= 0 ? informational[..plus] : informational;
    }

    private static string? ResolveShortBuildMetadata(string? informational)
    {
        var plus = informational?.IndexOf('+') ?? -1;
        if (plus < 0) { return null; }

        // Tolerate metadata shorter than 7 characters (git shas are longer, but nothing
        // guarantees the suffix is a sha) without an off-by-one or a throw.
        var metadata = informational![(plus + 1)..];
        return metadata.Length == 0 ? null : metadata[..Math.Min(7, metadata.Length)];
    }
}
