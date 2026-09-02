using Daqifi.Desktop.Exporter;
using Xunit;

namespace Daqifi.Avalonia.Tests.Exporter;

/// <summary>
/// Pins the wording and the Warning-vs-Error split that every user-chosen destination in the app
/// now shares — the CSV export dialog and both graph-save commands.
///
/// <para>
/// Constructed exceptions rather than real file operations, on purpose. Which errno a given vector
/// produces varies by platform, by file system, and by whether the run is root — the integration
/// tests in <c>GraphImageSaveTests</c> cover that, and one of them has to stand down on a root CI
/// runner. This class asks the narrower question those cannot answer portably: given an exception,
/// is it the destination's fault, and what is the user told?
/// </para>
/// </summary>
public sealed class DestinationFailureClassifierTests
{
    /// <summary>
    /// The three failures that are the destination's fault, and are therefore reported to the user
    /// as something they can act on and logged at Warning rather than raising a Sentry issue.
    /// </summary>
    [Fact]
    public void Destination_faults_are_recognised_as_blocked()
    {
        Assert.True(DestinationFailureClassifier.IsBlocked(new UnauthorizedAccessException()));
        Assert.True(DestinationFailureClassifier.IsBlocked(new DirectoryNotFoundException()));
    }

    /// <summary>
    /// The narrowness is the point: a bare <see cref="IOException"/> covers a full disk, a failing
    /// drive and an EF/SQLite read error, so it must keep the Error/Sentry path. Treating it as a
    /// destination fault would bury real defects under "choose a different folder".
    /// </summary>
    [Fact]
    public void Generic_failures_are_not_treated_as_the_destinations_fault()
    {
        // On a non-Windows host this is also the sharing-violation branch's answer, since that
        // check is gated to Windows; on Windows it is an HResult with no FACILITY_WIN32 facility.
        Assert.False(DestinationFailureClassifier.IsBlocked(new IOException("There is not enough space on the disk.")));
        Assert.False(DestinationFailureClassifier.IsBlocked(new InvalidOperationException()));
        Assert.False(DestinationFailureClassifier.IsBlocked(new TimeoutException()));
    }

    /// <summary>
    /// Each blocked failure names the file and says what to do about it. These sentences are what
    /// the user actually reads, on both the export dialog and the graph-save dialog.
    /// </summary>
    [Theory]
    [InlineData("access was denied")]
    [InlineData("Choose a different folder")]
    public void Denied_access_tells_the_user_to_pick_elsewhere(string expected)
    {
        var message = DestinationFailureClassifier.Describe(
            new UnauthorizedAccessException(), "/some/folder/graph.png", "the graph image");

        Assert.Contains("'graph.png'", message, StringComparison.Ordinal);
        Assert.Contains(expected, message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_folder_tells_the_user_the_location_is_gone()
    {
        var message = DestinationFailureClassifier.Describe(
            new DirectoryNotFoundException(), "/gone/graph.png", "the graph image");

        Assert.Contains("no longer exists", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unclassified failure still reaches the user, carrying whatever the system said, rather
    /// than being swallowed into a shrug.
    /// </summary>
    [Fact]
    public void Unclassified_failures_pass_the_systems_own_words_through()
    {
        var message = DestinationFailureClassifier.Describe(
            new IOException("There is not enough space on the disk."), "/some/folder/graph.png", "the graph image");

        Assert.Contains("Could not write 'graph.png'", message, StringComparison.Ordinal);
        Assert.Contains("There is not enough space on the disk.", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// With no path to name, the message uses the caller's own word for what it was writing — so a
    /// graph-save failure never calls the file "the export file", which is what a single shared
    /// fallback would have done once this classifier gained a second caller.
    /// </summary>
    [Theory]
    [InlineData("the graph image")]
    [InlineData("the export file")]
    public void A_blank_path_falls_back_to_the_callers_own_name_for_the_file(string fallback)
    {
        var message = DestinationFailureClassifier.Describe(new UnauthorizedAccessException(), "", fallback);

        Assert.Contains($"Could not write {fallback}", message, StringComparison.Ordinal);
    }
}
