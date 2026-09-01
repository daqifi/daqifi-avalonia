using System.Globalization;
using Daqifi.Core.Device.SdCard;
using Daqifi.Desktop.Loggers;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Tests for <see cref="ImportTimestampQuality"/>, the one thing that tells a user an imported
/// SD card session contains timestamps the device never sent.
///
/// Core substitutes the session's base time into any entry it cannot reconstruct a device tick
/// for, and reports that per entry via <see cref="SdCardLogEntry.HasDeviceTimestamp"/>. Those
/// samples land in the logging database looking exactly like measured ones — same table, same
/// columns, plotted on the same axis — so the warning built here is the only place the
/// substitution is ever stated. A missed warning is an invented measurement the user reads as
/// real; a spurious one sends them to re-run an experiment that was fine. Both directions are
/// pinned below.
/// </summary>
public class ImportTimestampQualityTests
{
    private static readonly DateTime BaseTime = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>An entry Core reconstructed from a real device tick.</summary>
    private static SdCardLogEntry Genuine(int secondsIn) =>
        new(BaseTime.AddSeconds(secondsIn), [1.0], 0u, null);

    /// <summary>An entry Core could not timestamp, so it substituted the session base time.</summary>
    private static SdCardLogEntry Substituted() =>
        new(BaseTime, [1.0], 0u, null) { HasDeviceTimestamp = false };

    /// <summary>
    /// Observes a file of <paramref name="genuineCount"/> real entries and
    /// <paramref name="substitutedCount"/> substituted ones, spread evenly through the file
    /// rather than bunched at one end — the count is the whole signal, and nothing here may
    /// depend on where the substituted entries fall.
    /// </summary>
    private static ImportTimestampQuality Observe(int genuineCount, int substitutedCount)
    {
        var quality = new ImportTimestampQuality();
        var total = genuineCount + substitutedCount;
        var emitted = 0;

        for (var i = 0; i < total; i++)
        {
            var due = (int)((long)(i + 1) * substitutedCount / total);
            if (due > emitted)
            {
                quality.Observe(Substituted());
                emitted++;
            }
            else
            {
                quality.Observe(Genuine(i));
            }
        }

        return quality;
    }

    /// <summary>
    /// Runs <paramref name="body"/> with the invariant culture in force. The warning formats its
    /// count and percentage for the user's locale, so a test asserting on the exact text has to
    /// state which locale it means rather than inherit the build agent's.
    /// </summary>
    private static void WithInvariantCulture(Action body)
    {
        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            body();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    #region Nothing to report

    [Fact]
    public void An_empty_import_reports_nothing()
    {
        // An empty log is an ordinary artefact of an interrupted logging session, not a fault.
        // Nothing was substituted because nothing was imported.
        var quality = new ImportTimestampQuality();

        Assert.Equal(0L, quality.TotalEntries);
        Assert.Equal(0L, quality.EntriesWithoutDeviceTimestamp);
        Assert.False(quality.HasDegenerateTimeAxis);
        Assert.False(quality.HasFlatTimeAxis);
        Assert.Equal(0.0, quality.SubstitutedFraction, 12);
        Assert.Null(quality.BuildUserWarning());
    }

    [Fact]
    public void A_file_whose_timestamps_are_all_genuine_reports_nothing()
    {
        var quality = Observe(genuineCount: 500, substitutedCount: 0);

        Assert.Equal(500L, quality.TotalEntries);
        Assert.False(quality.HasDegenerateTimeAxis);
        Assert.Null(quality.BuildUserWarning());
    }

    [Fact]
    public void Genuine_entries_sharing_one_timestamp_are_not_a_substitution()
    {
        // The signal is Core's per-entry flag, not the shape of the time axis. A device that
        // stamps several samples with one tick produces identical timestamps that are all real,
        // and the tick-equality inference this replaced called exactly that a fabricated axis.
        var quality = new ImportTimestampQuality();
        for (var i = 0; i < 10; i++)
        {
            quality.Observe(Genuine(0));
        }

        Assert.Equal(10L, quality.TotalEntries);
        Assert.Equal(0L, quality.EntriesWithoutDeviceTimestamp);
        Assert.False(quality.HasFlatTimeAxis);
        Assert.False(quality.HasDegenerateTimeAxis);
        Assert.Null(quality.BuildUserWarning());
    }

    #endregion

    #region Any substitution at all is reported

    [Fact]
    public void One_substituted_sample_in_a_thousand_is_still_reported()
    {
        // The implementation this replaced stayed silent below a 20% margin, so this file — and
        // anything up to one sample in five — imported with no warning whatsoever.
        var quality = Observe(genuineCount: 999, substitutedCount: 1);

        Assert.Equal(1000L, quality.TotalEntries);
        Assert.Equal(1L, quality.EntriesWithoutDeviceTimestamp);
        Assert.True(quality.HasDegenerateTimeAxis);
        Assert.False(quality.HasFlatTimeAxis);

        var warning = quality.BuildUserWarning();
        Assert.NotNull(warning);
        Assert.Contains("no usable timestamp", warning, StringComparison.Ordinal);
    }

    [Theory]
    // 19 of 99 — a fifth of the file, one shy of the old margin: the worst case it let through.
    [InlineData(80, 19, "19", "19.2")]
    [InlineData(50, 50, "50", "50.0")]
    public void The_warning_names_the_count_and_the_percentage(
        int genuineCount, int substitutedCount, string expectedCount, string expectedPercent)
    {
        WithInvariantCulture(() =>
        {
            var quality = Observe(genuineCount, substitutedCount);

            var warning = quality.BuildUserWarning();

            Assert.NotNull(warning);
            Assert.Contains($"{expectedCount} of the samples", warning, StringComparison.Ordinal);
            Assert.Contains($"({expectedPercent}%)", warning, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_share_too_small_to_render_reports_as_less_than_a_tenth_of_a_percent()
    {
        // 1 in 5,000 rounds to 0.0% at the format's resolution. Printing "0.0%" beside a
        // non-zero count would contradict itself in one sentence, so it reads "<0.1%" instead.
        WithInvariantCulture(() =>
        {
            var quality = Observe(genuineCount: 4_999, substitutedCount: 1);

            var warning = quality.BuildUserWarning();

            Assert.NotNull(warning);
            Assert.Contains("(<0.1%)", warning, StringComparison.Ordinal);
            Assert.DoesNotContain("(0.0%)", warning, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void A_file_with_no_device_timestamps_at_all_reports_a_flat_axis()
    {
        var quality = Observe(genuineCount: 0, substitutedCount: 40);

        Assert.True(quality.HasFlatTimeAxis);
        Assert.True(quality.HasDegenerateTimeAxis);
        Assert.Equal(1.0, quality.SubstitutedFraction, 12);

        var warning = quality.BuildUserWarning();
        Assert.NotNull(warning);
        Assert.Contains("time axis will be flat", warning, StringComparison.Ordinal);
    }

    #endregion

    #region Guard clauses

    [Fact]
    public void Observe_rejects_a_null_entry()
    {
        var quality = new ImportTimestampQuality();

        Assert.Throws<ArgumentNullException>(() => quality.Observe(null!));
    }

    #endregion
}
