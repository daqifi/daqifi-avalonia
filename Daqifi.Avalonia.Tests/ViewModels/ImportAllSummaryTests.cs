using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Tests for <see cref="DeviceLogsViewModel.BuildImportAllSummary"/>, the text of the dialog that
/// closes an "Import All" run.
///
/// It used to say "N file(s) failed to import." and nothing else — no names, and guidance only in
/// the case where nothing at all imported. A user with sixty logs on a card and three bad ones had
/// no way to find out which three, and no advice about them. The wording is separated from the
/// view model so it can be asserted without a dialog or a device.
/// </summary>
public class ImportAllSummaryTests
{
    private static SdCardFailure Failure(string guidance) =>
        new(SdCardState.Error, "status", guidance, IsExpectedDeviceCondition: true, IsCardUnavailable: true);

    [Fact]
    public void A_clean_run_says_only_what_it_imported()
    {
        var summary = DeviceLogsViewModel.BuildImportAllSummary(
            new ImportAllOutcome { TotalCount = 4, ImportedCount = 4 });

        Assert.Equal("Imported 4 of 4 files.", summary);
    }

    [Fact]
    public void Skipped_files_are_named()
    {
        var outcome = new ImportAllOutcome { TotalCount = 3, ImportedCount = 1 };
        outcome.RecordSkip("LOG_0002.bin", "advice A");
        outcome.RecordSkip("LOG_0003.bin", "advice A");

        var summary = DeviceLogsViewModel.BuildImportAllSummary(outcome);

        Assert.Contains("Imported 1 of 3 files.", summary, StringComparison.Ordinal);
        Assert.Contains("Skipped 2 file(s).", summary, StringComparison.Ordinal);
        Assert.Contains("LOG_0002.bin", summary, StringComparison.Ordinal);
        Assert.Contains("LOG_0003.bin", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Identical_advice_is_given_once_and_distinct_advice_once_each()
    {
        // Ten empty logs need their sentence once; a card holding an empty log and a corrupt one
        // needs both sentences.
        var outcome = new ImportAllOutcome { TotalCount = 4, ImportedCount = 1 };
        outcome.RecordSkip("a.bin", "advice A");
        outcome.RecordSkip("b.bin", "advice A");
        outcome.RecordSkip("c.bin", "advice B");

        var summary = DeviceLogsViewModel.BuildImportAllSummary(outcome);

        Assert.Equal(1, CountOccurrences(summary, "advice A"));
        Assert.Equal(1, CountOccurrences(summary, "advice B"));
        Assert.Equal(["advice A", "advice B"], outcome.SkipGuidance);
    }

    [Fact]
    public void A_long_list_of_skipped_files_is_capped_and_the_rest_counted()
    {
        var outcome = new ImportAllOutcome { TotalCount = 20, ImportedCount = 8 };
        for (var i = 0; i < 12; i++)
        {
            outcome.RecordSkip($"LOG_{i:D4}.bin", "advice");
        }

        var summary = DeviceLogsViewModel.BuildImportAllSummary(outcome);

        Assert.Contains("LOG_0009.bin", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("LOG_0010.bin", summary, StringComparison.Ordinal);
        Assert.Contains("...and 2 more", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_stopped_by_a_card_wide_failure_names_the_file_it_stopped_at()
    {
        var outcome = new ImportAllOutcome
        {
            TotalCount = 9,
            ImportedCount = 2,
            AbortingFailure = Failure("power-cycle the device"),
            AbortedOnFile = "LOG_0003.bin",
        };

        var summary = DeviceLogsViewModel.BuildImportAllSummary(outcome);

        Assert.Contains("Import stopped at LOG_0003.bin: power-cycle the device", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_run_cut_short_by_a_disconnect_says_so_instead_of_reading_as_complete()
    {
        // Port-only: this app re-checks IsConnected every iteration, so a disconnect has no
        // failing file to name. Without this line the dialog would look like a finished run.
        var outcome = new ImportAllOutcome
        {
            TotalCount = 9,
            ImportedCount = 2,
            DisconnectedMidBatch = true,
        };

        var summary = DeviceLogsViewModel.BuildImportAllSummary(outcome);

        Assert.Contains("the device disconnected", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void A_disconnect_wins_over_a_card_wide_failure_it_caused()
    {
        // The disconnect is the cause; reporting both would give the user two explanations for
        // one event, one of which points at the SD card.
        var outcome = new ImportAllOutcome
        {
            TotalCount = 9,
            ImportedCount = 2,
            DisconnectedMidBatch = true,
            AbortingFailure = Failure("power-cycle the device"),
            AbortedOnFile = "LOG_0003.bin",
        };

        var summary = DeviceLogsViewModel.BuildImportAllSummary(outcome);

        Assert.Contains("the device disconnected", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("Import stopped at", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Substituted_timestamps_are_reported_for_the_batch_too()
    {
        var outcome = new ImportAllOutcome
        {
            TotalCount = 5,
            ImportedCount = 5,
            TimestampWarningCount = 2,
        };

        var summary = DeviceLogsViewModel.BuildImportAllSummary(outcome);

        Assert.Contains("2 file(s) contain samples with no device timestamp", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void BuildImportAllSummary_rejects_a_null_outcome()
    {
        Assert.Throws<ArgumentNullException>(() => DeviceLogsViewModel.BuildImportAllSummary(null!));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
