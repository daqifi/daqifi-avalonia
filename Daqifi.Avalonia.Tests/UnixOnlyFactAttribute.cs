using Xunit;

namespace Daqifi.Avalonia.Tests;

/// <summary>
/// A <see cref="FactAttribute"/> for a row that can only run on Linux or macOS, which the runner
/// reports as <b>Skipped</b> on Windows instead of silently as Passed.
///
/// <para>
/// The problem it exists to solve (issue #421): a row that stands itself down with
/// <c>if (OperatingSystem.IsWindows()) { return; }</c> still reports <b>Passed</b>, because from
/// the runner's point of view the method was called and did not throw. Nothing in the output
/// distinguishes "this assertion held" from "this assertion was never attempted", so a Windows
/// developer reads a green suite that in fact exercised none of these vectors.
/// </para>
///
/// <para>
/// <b>Why a subclass and not <c>Assert.Skip</c>:</b> the project pins xunit <b>2.9.3</b>, where
/// dynamic (mid-run) skipping does not exist — there is no <c>Assert.Skip</c> and no
/// <c>DynamicSkipToken</c> in the pinned assemblies. What v2 does support is a skip decided at
/// <i>discovery</i> time: xunit reads <c>Skip</c> off the constructed attribute instance, so a
/// subclass that sets it in its constructor turns the row into a genuine skip with a reason. That
/// costs one file and no package — <c>Xunit.SkippableFact</c> would buy the same thing for a new
/// dependency and a lock-file refresh.
/// </para>
///
/// <para>
/// It also sidesteps <c>xUnit1004</c> ("test methods should not be skipped"), which fires on a
/// literal <c>[Fact(Skip = "…")]</c> written in source and would be an error here, since this
/// project builds with <c>TreatWarningsAsErrors</c>.
/// </para>
///
/// <para>
/// <b>This attribute does not replace the in-body platform guard, and must not be read as
/// doing so.</b> <c>CA1416</c> narrows a method's supported platforms by control flow, not by
/// attribute, so a row calling <c>File.SetUnixFileMode</c> or <c>File.GetUnixFileMode</c> fails to
/// compile the moment its <c>if (OperatingSystem.IsWindows()) { … return; }</c> is deleted. The
/// guard therefore stays — but it becomes an assertion rather than a silent return, so that if
/// this attribute ever stops taking effect the row fails loudly instead of going back to reporting
/// Passed without running.
/// </para>
/// </summary>
public sealed class UnixOnlyFactAttribute : FactAttribute
{
    /// <summary>The reason the runner prints, and the text an in-body fail-safe should echo.</summary>
    internal const string Reason =
        "Unix-only: this vector is about Unix file-mode/link semantics, which Windows does not have.";

    public UnixOnlyFactAttribute()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip = Reason;
        }
    }
}
