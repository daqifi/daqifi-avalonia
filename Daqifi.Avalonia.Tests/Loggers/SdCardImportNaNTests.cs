using System.Runtime.CompilerServices;
using System.Text;
using Daqifi.Core.Device.SdCard;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.Loggers;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Issue #412: one <see cref="double.NaN"/> reading in an SD card log failed the whole import.
///
/// <para>Core's parsers pass a NaN through (the CSV parser accepts <c>NaN</c> and <c>nan</c>, the
/// <c>.bin</c> float leg takes the float as-is), and Microsoft.Data.Sqlite refuses to bind one —
/// <c>InvalidOperationException: Cannot store 'NaN' values.</c> — so the batch holding it failed,
/// and with it every reading the parser had not yet reached. A NaN in the first batch left nothing
/// at all; a later one left an <see cref="SessionStatus.ImportFailed"/> fragment. Either way the
/// user got "Import Failed" and an Error-level log line, which is a Sentry event.</para>
///
/// <para>The live path settled this in #294: a NaN is dropped at <c>SessionSampleWriter.Add</c> and
/// reported. It guards NaN only, because that is all the database refuses — an infinity stores
/// and reads back — and these tests pin the importer to the same rule.</para>
/// </summary>
public sealed class SdCardImportNaNTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "sd-import-nan-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "DAQiFiDatabase.db");

    private static readonly DateTime SampleBase = new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly RecordingAppLogger _log = new();

    public SdCardImportNaNTests()
    {
        Directory.CreateDirectory(_directory);
        DatabaseMigrator.ApplyMigrations(Factory(), DatabasePath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task A_NaN_in_the_first_batch_costs_only_that_reading()
    {
        var result = await Importer().ImportSessionAsync(
            Log(Entries(12, nanAt: [3])), options: null, progress: null, ct: CancellationToken.None);

        Assert.True(result.SessionPersisted);
        Assert.Equal(11, result.SamplesImported);

        var session = Assert.Single(AllSessions());
        Assert.Equal(SessionStatus.Complete, session.Status);
        Assert.Equal(11, session.SampleCount);

        // Every good reading landed, and exactly the good ones: the NaN's neighbours keep their
        // own values and nothing was stored in the NaN's place.
        Assert.Equal(
            Enumerable.Range(0, 12).Where(i => i != 3).Select(ValueOf),
            StoredValues(session.ID));
    }

    [Fact]
    public async Task A_NaN_in_a_later_batch_no_longer_drops_the_rest_of_the_file()
    {
        // Past the first commit, which is where the unguarded import used to stop and flag the
        // session as ImportFailed, keeping only what had already landed.
        var total = SdCardSessionImporter.BatchSize * 2 + 500;
        var nanAt = SdCardSessionImporter.BatchSize + 200;

        var result = await Importer().ImportSessionAsync(
            Log(Entries(total, nanAt: [nanAt])), options: null, progress: null, ct: CancellationToken.None);

        Assert.True(result.SessionPersisted);
        Assert.Equal(total - 1, result.SamplesImported);

        var session = Assert.Single(AllSessions());
        Assert.Equal(SessionStatus.Complete, session.Status);
        Assert.Equal(total - 1, SampleCountFor(session.ID));

        // The reading after the NaN and the last reading of the file both made it.
        var stored = StoredValues(session.ID);
        Assert.Contains(ValueOf(nanAt + 1), stored);
        Assert.Contains(ValueOf(total - 1), stored);
    }

    [Fact]
    public async Task The_user_is_told_readings_were_left_out()
    {
        var result = await Importer().ImportSessionAsync(
            Log(Entries(12, nanAt: [3, 7])), options: null, progress: null, ct: CancellationToken.None);

        // OutcomeGuidance is what every import surface appends to its dialog: the single-file
        // import from disk, the single-file import from a device, and Import All's notices.
        Assert.True(result.SessionPersisted);
        Assert.Contains("NaN", result.OutcomeGuidance);
        Assert.Contains("left out", result.OutcomeGuidance);
    }

    [Fact]
    public async Task A_clean_log_says_nothing_about_left_out_readings()
    {
        var result = await Importer().ImportSessionAsync(
            Log(Entries(12)), options: null, progress: null, ct: CancellationToken.None);

        Assert.Equal(string.Empty, result.OutcomeGuidance);
        Assert.DoesNotContain(_log.Warnings, w => w.Contains("NaN"));
    }

    [Fact]
    public async Task A_left_out_reading_is_a_warning_not_an_error()
    {
        await Importer().ImportSessionAsync(
            Log(Entries(12, nanAt: [3, 7])), options: null, progress: null, ct: CancellationToken.None);

        // An Error line is a Sentry event, and the unguarded import raised one for what is a
        // property of the user's file, not a fault in the app.
        Assert.Empty(_log.Errors);

        // One summary line for the whole import, not one per reading: a file whose every reading
        // on a channel is NaN must not write a log line per sample.
        var warning = Assert.Single(_log.Warnings, w => w.Contains("NaN"));
        Assert.Contains("2 reading", warning);
    }

    [Fact]
    public async Task A_log_whose_every_reading_is_NaN_is_reported_as_that_not_as_an_empty_file()
    {
        var result = await Importer().ImportSessionAsync(
            Log(Entries(5, nanAt: [0, 1, 2, 3, 4])), options: null, progress: null, ct: CancellationToken.None);

        Assert.False(result.SessionPersisted);
        Assert.Equal(0, result.SamplesImported);
        Assert.Empty(AllSessions());
        Assert.Empty(_log.Errors);

        // "The log contained no samples" would be untrue — it held five, none storable.
        Assert.Contains("NaN", result.OutcomeGuidance);
        Assert.DoesNotContain("contained no samples", result.OutcomeGuidance);
    }

    /// <summary>
    /// End to end through the real parser, from the file the user picks: the CSV parser accepts
    /// the lower-case <c>nan</c> C's <c>printf("%f")</c> writes.
    /// </summary>
    [Fact]
    public async Task A_firmware_csv_carrying_nan_imports_through_the_file_entry_point()
    {
        var path = WriteCsv("log_20260901_120000_nan.csv",
            "1000,100,1000,200",
            "2000,nan,2000,201",
            "3000,102,3000,202");

        var result = await Importer().ImportFromFileAsync(path);

        Assert.True(result.SessionPersisted);
        Assert.Equal(5, result.SamplesImported);
        Assert.Equal(5, SampleCountFor(result.Session.ID));
        Assert.Empty(_log.Errors);
    }

    /// <summary>
    /// The other half of the measurement behind the guard: an infinity is NOT refused by the
    /// database, so — like #294's live-path guard — the importer keeps it rather than discarding a
    /// reading it could have stored.
    /// </summary>
    [Fact]
    public async Task Infinities_are_stored_not_dropped()
    {
        var path = WriteCsv("log_20260901_120000_inf.csv",
            "1000,100,1000,200",
            "2000,1e400,2000,-Infinity",
            "3000,102,3000,202");

        var result = await Importer().ImportFromFileAsync(path);

        Assert.True(result.SessionPersisted);
        Assert.Equal(6, result.SamplesImported);
        Assert.Equal(string.Empty, result.OutcomeGuidance);

        var stored = StoredValues(result.Session.ID);
        Assert.Contains(double.PositiveInfinity, stored);
        Assert.Contains(double.NegativeInfinity, stored);
    }

    #region Fixtures

    private SdCardSessionImporter Importer() =>
        new(Factory(), SdCardSessionImporter.DOWNLOAD_STALL_TIMEOUT, _log);

    /// <summary>No device configuration, so one analog channel is discovered and each entry is one row.</summary>
    private static SdCardLogSession Log(IAsyncEnumerable<SdCardLogEntry> samples) =>
        new("log_nan.bin", SampleBase, deviceConfig: null, samples);

    private static double ValueOf(int index) => index * 0.5;

    private static async IAsyncEnumerable<SdCardLogEntry> Entries(
        int count,
        int[]? nanAt = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var value = nanAt?.Contains(i) == true ? double.NaN : ValueOf(i);
            yield return new SdCardLogEntry(SampleBase.AddMilliseconds(i), [value], 0u, null);
            await Task.Yield();
        }
    }

    /// <summary>The firmware's CSV log format: a comment header, then timestamp/value column pairs.</summary>
    private string WriteCsv(string fileName, params string[] rows)
    {
        var csv = new StringBuilder();
        csv.Append("# Device: Nyquist 1\n# Serial Number: AABBCCDDEEFF0011\n");
        csv.Append("# Timestamp Tick Rate: 50000000 Hz\n");
        csv.Append("ain0_ts,ain0_val,ain1_ts,ain1_val\n");
        foreach (var row in rows)
        {
            csv.Append(row).Append('\n');
        }

        var path = Path.Combine(_directory, fileName);
        File.WriteAllText(path, csv.ToString(), Encoding.ASCII);
        return path;
    }

    private IDbContextFactory<LoggingContext> Factory() => TestDatabase.Contexts(DatabasePath);

    private List<LoggingSession> AllSessions()
    {
        using var context = Factory().CreateDbContext();
        return context.Sessions.AsNoTracking().OrderBy(s => s.ID).ToList();
    }

    private long SampleCountFor(int sessionId)
    {
        using var context = Factory().CreateDbContext();
        return context.Samples.LongCount(s => s.LoggingSessionID == sessionId);
    }

    private List<double> StoredValues(int sessionId)
    {
        using var context = Factory().CreateDbContext();
        return context.Samples
            .Where(s => s.LoggingSessionID == sessionId)
            .OrderBy(s => s.TimestampTicks)
            .Select(s => s.Value)
            .ToList();
    }

    #endregion
}
