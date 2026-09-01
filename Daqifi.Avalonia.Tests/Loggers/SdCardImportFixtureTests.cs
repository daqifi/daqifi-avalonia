using System.Globalization;
using Daqifi.Core.Device.SdCard;
using Daqifi.Desktop.Loggers;
using Google.Protobuf;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// End-to-end tests for the SD import's timestamp reporting, driven by real log-file bytes
/// through Core's own parsers rather than by hand-built <see cref="SdCardLogEntry"/> objects.
///
/// <para>Two of the three inputs are files checked in under <c>Fixtures/</c>. <c>empty-log.bin</c>
/// is the artefact the ticket is about — a zero-byte log, which is what an interrupted logging
/// session leaves on a FAT card — and it is meaningful precisely as a file on disk.
/// <c>duplicate-device-ticks.csv</c> is a firmware CSV log, human-readable, whose rows repeat the
/// device tick. The third input is a protobuf <c>.bin</c> log assembled below from
/// <see cref="DaqifiOutMessage"/> in the same varint-delimited framing the firmware writes: it is
/// built in code rather than checked in as a blob so a reviewer can see which messages carry a
/// device timestamp and which do not, which is the entire point of the case.</para>
/// </summary>
public class SdCardImportFixtureTests
{
    /// <summary>Fixed so a parse is reproducible; the parser otherwise anchors on DateTime.UtcNow.</summary>
    private static readonly DateTime SessionStart = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private static string FixturePath(string fileName) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private static SdCardParseOptions Options() => new() { SessionStartTime = SessionStart };

    private static async Task<List<SdCardLogEntry>> ReadEntriesAsync(SdCardLogSession session)
    {
        var entries = new List<SdCardLogEntry>();
        await foreach (var entry in session.Samples)
        {
            entries.Add(entry);
        }

        return entries;
    }

    private static ImportTimestampQuality Measure(IEnumerable<SdCardLogEntry> entries)
    {
        var quality = new ImportTimestampQuality();
        foreach (var entry in entries)
        {
            quality.Observe(entry);
        }

        return quality;
    }

    /// <summary>
    /// The share of entries sharing the first entry's tick — the inference this import used before
    /// it could read Core's per-entry flag, reproduced here so each fixture can state what the old
    /// code would have concluded about it. It warned at or above 0.2 and said nothing below.
    /// </summary>
    private static double CollapsedFractionOfOldInference(IReadOnlyList<SdCardLogEntry> entries)
    {
        if (entries.Count <= 1)
        {
            return 0.0;
        }

        var firstTicks = entries[0].Timestamp.Ticks;
        var atFirst = entries.Count(e => e.Timestamp.Ticks == firstTicks);
        return (atFirst - 1) / (double)(entries.Count - 1);
    }

    #region An empty log is an ordinary file

    [Fact]
    public async Task A_zero_byte_log_parses_as_an_empty_session_and_warns_about_nothing()
    {
        var path = FixturePath("empty-log.bin");
        Assert.Equal(0, new FileInfo(path).Length);

        var session = await SdCardFileParserFactory.ParseFileAsync(path, Options());
        var entries = await ReadEntriesAsync(session);

        Assert.Empty(entries);

        var quality = Measure(entries);
        Assert.Equal(0L, quality.TotalEntries);
        Assert.False(quality.HasDegenerateTimeAxis);
        Assert.Null(quality.BuildUserWarning());
    }

    #endregion

    #region Partly substituted timestamps

    /// <summary>
    /// Builds a protobuf SD log: one status message stating the tick rate, then
    /// <paramref name="sampleCount"/> stream messages of which the ones at
    /// <paramref name="substitutedIndices"/> carry <c>msg_time_stamp == 0</c> — the shape Core
    /// reports as <see cref="SdCardLogEntry.HasDeviceTimestamp"/> <c>false</c> and substitutes the
    /// session base time into.
    ///
    /// The substituted indices are deliberately non-adjacent: Core merges consecutive messages
    /// that share a timestamp into one entry, so two neighbouring zeros would collapse into a
    /// single sample and the file would no longer have the shape being tested.
    /// </summary>
    private static byte[] BuildProtobufLog(int sampleCount, params int[] substitutedIndices)
    {
        using var stream = new MemoryStream();

        // Status header: no analog or digital payload, so Core reads it as configuration only.
        var status = new DaqifiOutMessage { TimestampFreq = 1_000, AnalogInPortNum = 1 };
        status.WriteDelimitedTo(stream);

        for (var i = 0; i < sampleCount; i++)
        {
            var message = new DaqifiOutMessage
            {
                // A tick of zero is how firmware reports "no usable timestamp for this sample".
                MsgTimeStamp = substitutedIndices.Contains(i) ? 0u : (uint)((i + 1) * 1_000),
            };
            message.AnalogInDataFloat.Add(1.0f + i);
            message.WriteDelimitedTo(stream);
        }

        return stream.ToArray();
    }

    [Fact]
    public async Task A_log_with_three_samples_in_twenty_substituted_names_the_count_and_the_share()
    {
        // 15% — below the 20% margin the old inference required, so this file imported with no
        // warning at all and its three invented timestamps went into the database unremarked.
        var bytes = BuildProtobufLog(sampleCount: 20, substitutedIndices: [4, 10, 16]);

        using var stream = new MemoryStream(bytes);
        var session = await SdCardFileParserFactory.ParseAsync(stream, "partial-timestamps.bin", Options());
        var entries = await ReadEntriesAsync(session);

        Assert.Equal(20, entries.Count);
        Assert.Equal(3, entries.Count(e => !e.HasDeviceTimestamp));

        // What the old inference would have made of this file: silent, being under its margin.
        Assert.True(
            CollapsedFractionOfOldInference(entries) < 0.2,
            "the fixture is meant to sit below the old 20% margin");

        var quality = Measure(entries);

        Assert.Equal(3L, quality.EntriesWithoutDeviceTimestamp);
        Assert.True(quality.HasDegenerateTimeAxis);
        Assert.False(quality.HasFlatTimeAxis);

        var original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            var warning = quality.BuildUserWarning();
            Assert.NotNull(warning);
            Assert.Contains("3 of the samples", warning, StringComparison.Ordinal);
            Assert.Contains("(15.0%)", warning, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public async Task A_log_whose_every_sample_carries_a_device_tick_warns_about_nothing()
    {
        var bytes = BuildProtobufLog(sampleCount: 20);

        using var stream = new MemoryStream(bytes);
        var session = await SdCardFileParserFactory.ParseAsync(stream, "healthy.bin", Options());
        var entries = await ReadEntriesAsync(session);

        Assert.Equal(20, entries.Count);
        Assert.All(entries, e => Assert.True(e.HasDeviceTimestamp));

        Assert.Null(Measure(entries).BuildUserWarning());
    }

    #endregion

    #region Repeated device ticks are real data

    [Fact]
    public async Task A_csv_log_that_repeats_the_device_tick_is_not_reported_as_fabricated()
    {
        // Every row of this fixture carries a timestamp the device wrote; the device simply
        // reported several samples against the same tick. Core says so — HasDeviceTimestamp is
        // true for all six entries — but the tick-equality inference this replaced saw a run of
        // identical timestamps and told the user 40% of their samples had been invented.
        var session = await SdCardFileParserFactory.ParseFileAsync(
            FixturePath("duplicate-device-ticks.csv"), Options());
        var entries = await ReadEntriesAsync(session);

        Assert.Equal(6, entries.Count);
        Assert.All(entries, e => Assert.True(e.HasDeviceTimestamp));

        // The repeated ticks really do collapse onto one reconstructed timestamp, which is what
        // made the old inference fire: without this the fixture would prove nothing.
        Assert.True(
            CollapsedFractionOfOldInference(entries) >= 0.2,
            "the fixture is meant to trip the old 20% margin");

        var quality = Measure(entries);

        Assert.Equal(6L, quality.TotalEntries);
        Assert.Equal(0L, quality.EntriesWithoutDeviceTimestamp);
        Assert.False(quality.HasDegenerateTimeAxis);
        Assert.Null(quality.BuildUserWarning());
    }

    #endregion
}
