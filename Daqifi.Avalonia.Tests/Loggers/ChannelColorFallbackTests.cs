using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Logger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using OxyPlot;
using Xunit;
using ChannelType = Daqifi.Core.Channel.ChannelType;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Opening an OLD session database must not take the app down, or quietly drop a channel (issue #231).
///
/// <para>A sample row carries the colour its channel was drawn in, and the session viewer hands that
/// string straight to <see cref="OxyColor.Parse"/>. Measured against OxyPlot.Core 2.2.0, that has
/// TWO bad outcomes rather than one, and they are not the ones the issue assumed:</para>
/// <list type="bullet">
/// <item>a BLANK or unparseable string throws <see cref="FormatException"/>, and the throw escapes
/// channel discovery — one bad row does not lose one series, it aborts the whole session load;</item>
/// <item>a NULL does not throw at all. It parses to <c>OxyColors.Undefined</c>, whose alpha is zero
/// — so the channel loads, takes a legend row, and is drawn COMPLETELY INVISIBLE. Silent, and worse
/// than the crash for being silent. <see cref="A_null_colour_parses_to_an_invisible_series"/> pins
/// that, since it is the whole reason null needs guarding rather than passing through.</item>
/// </list>
///
/// <para>The parity ledger first dismissed all of this as unreachable, because the migration declares
/// <c>Samples.Color</c> <c>NOT NULL</c>. That constraint binds the table AS THE MIGRATION CREATES
/// IT; SQLite does not re-validate rows already sitting in a database file, and upstream
/// daqifi-desktop's own comment above <c>FALLBACK_CHANNEL_COLOR</c> names exactly that case
/// ("legacy/imported rows can omit it"). <see cref="A_legacy_database_can_hold_a_null_colour"/>
/// settles the argument by building such a file and reading the null back out of it, so the rest of
/// these tests pin a reachable failure rather than a hypothetical one. The empty string needs no old
/// file at all: <c>NOT NULL</c> has always accepted <c>""</c>.</para>
///
/// <para>Every assertion below fails against the unchanged code, which is the evidence that the
/// defect was real. The blank case is the one a <c>?? fallback</c> alone would have left wide open.</para>
/// </summary>
public class ChannelColorFallbackTests : IDisposable
{
    /// <summary>
    /// The colour a channel with no usable colour is drawn in: upstream's
    /// <c>SessionDataRepository.FALLBACK_CHANNEL_COLOR</c>, matched exactly so the two apps render
    /// the same legacy session the same way. Written out here rather than read off the production
    /// constant — a test that quotes the constant back at itself would follow a change to it.
    /// </summary>
    private static readonly OxyColor Fallback = OxyColor.Parse("#FF808080");

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "colour-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_directory, "DAQiFiDatabase.db");

    public ChannelColorFallbackTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, recursive: true); } catch { /* best-effort cleanup */ }
        GC.SuppressFinalize(this);
    }

    #region The reachability argument

    /// <summary>
    /// The premise the whole issue rests on, demonstrated rather than asserted: a database file
    /// whose <c>Samples</c> table predates the <c>NOT NULL</c> can hold a null colour, and the
    /// current schema reads it back as a null — the migration constrains new tables, not old rows.
    /// </summary>
    [Fact]
    public void A_legacy_database_can_hold_a_null_colour()
    {
        var factory = SeedLegacySession(nullColour: true);

        var channel = Assert.Single(new SessionDataRepository(factory, new SilentLogger())
            .LoadInitialSession(SessionId).Channels);

        Assert.Null(channel.Color);
    }

    /// <summary>
    /// Why a null has to be intercepted rather than left to <see cref="OxyColor.Parse"/>: it does not
    /// throw on one. It yields <c>OxyColors.Undefined</c> — alpha zero — and OxyPlot only substitutes
    /// a palette colour for <c>Automatic</c>, not for <c>Undefined</c>, so the series is rendered
    /// fully transparent and the channel simply is not on the plot.
    /// </summary>
    /// <remarks>
    /// A characterisation test of a third-party library, deliberately: this behaviour is the premise
    /// of the null half of the fix, and if a future OxyPlot bump starts throwing here instead, the
    /// premise has changed and it should be this test that says so rather than a bug report.
    /// </remarks>
    [Fact]
    public void A_null_colour_parses_to_an_invisible_series()
    {
        var parsed = OxyColor.Parse(null!);

        Assert.Equal(OxyColors.Undefined, parsed);
        Assert.Equal(0, parsed.A);
    }

    #endregion

    #region The crash, end to end

    /// <summary>
    /// The user-visible failure: open a session logged before the colour column was required and the
    /// load throws out of series construction. Both halves are asserted — that it does not throw,
    /// and that the channel is still THERE and visible, because "no crash" bought by dropping the
    /// series would lose the user's data just as quietly.
    /// </summary>
    [Theory]
    [InlineData(true)]   // the row holds SQL NULL
    [InlineData(false)]  // the row holds "", which the NOT NULL column has always permitted
    public void A_session_whose_stored_colour_is_missing_still_loads_and_plots(bool nullColour)
    {
        var factory = SeedLegacySession(nullColour);
        var repository = new SessionDataRepository(factory, new SilentLogger());

        var channel = Assert.Single(repository.LoadInitialSession(SessionId).Channels);

        var plotFactory = new PlotModelFactory();
        var plotModel = plotFactory.CreateMainPlotModel();

        var (series, legendItem) = plotFactory.CreateChannelSeries(
            channel.ChannelName, channel.DeviceSerialNo, channel.Type, channel.Color, plotModel, null);

        Assert.Equal("AI0", series.Title);
        Assert.True(series.IsVisible);
        Assert.Equal(Fallback, series.Color);

        // The legend swatch is handed the series' colour, so it agrees with the line rather than
        // showing a chip for a colour nothing was drawn in.
        Assert.Equal(Fallback, legendItem.SeriesColor);
    }

    #endregion

    #region The series factory, directly

    /// <summary>
    /// Every shape a stored colour can arrive in that cannot be drawn. Null and blank are what a
    /// legacy row holds; the unparseable ones are what a hand-edited or half-migrated row holds, and
    /// they fail from the same line — so guarding only for null would leave the series factory one
    /// bad string away from the crash, which is precisely what review caught on the first attempt at
    /// this fix.
    /// </summary>
    /// <remarks>
    /// Not asserted here: a truncated hex like <c>"#FFD32F2"</c>. OxyPlot parses ANY hex-shaped string
    /// it can widen to a <c>uint</c>, so that one yields a valid-but-wrong colour rather than failing,
    /// and nothing short of re-implementing the format grammar would catch it. Out of scope — this
    /// change makes an unusable colour fall back, it does not add validation OxyPlot does not do.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a colour")]
    [InlineData("#GGGGGG")]
    public void An_unusable_stored_colour_draws_the_series_in_the_fallback(string? storedColour)
    {
        var plotFactory = new PlotModelFactory();

        var (series, _) = plotFactory.CreateChannelSeries(
            "AI0", "SN-1", ChannelType.Analog, storedColour!, plotFactory.CreateMainPlotModel(), null);

        Assert.Equal(Fallback, series.Color);
    }

    /// <summary>
    /// The guard on the above: a colour the database DOES hold properly must still be used verbatim.
    /// A fallback that swallowed good values would turn every plot grey and no other test here would
    /// notice, since they all assert the fallback.
    /// </summary>
    [Theory]
    [InlineData("#FFD32F2F")]
    [InlineData("#ffd32f2f")]  // Avalonia writes upper case, but case must not decide the colour
    [InlineData("#D32F2F")]    // the alpha-less form OxyColor.Parse also accepts
    public void A_usable_stored_colour_is_used_as_it_is(string storedColour)
    {
        var plotFactory = new PlotModelFactory();

        var (series, _) = plotFactory.CreateChannelSeries(
            "AI0", "SN-1", ChannelType.Analog, storedColour, plotFactory.CreateMainPlotModel(), null);

        Assert.Equal(OxyColor.Parse("#FFD32F2F"), series.Color);
    }

    #endregion

    #region Fixture

    private const int SessionId = 1;

    /// <summary>
    /// A database file shaped like one written before <c>Samples.Color</c> was required: the current
    /// schema everywhere else, but a <c>Samples</c> table whose <c>Color</c> column is nullable, with
    /// one session and one sample row in it whose colour is missing.
    /// </summary>
    /// <param name="nullColour">
    /// <see langword="true"/> stores SQL NULL — only possible in the older table. <see langword="false"/>
    /// stores <c>""</c>, which the CURRENT <c>NOT NULL</c> column accepts perfectly happily, so that
    /// half of the exposure does not even need an old file.
    /// </param>
    private IDbContextFactory<LoggingContext> SeedLegacySession(bool nullColour)
    {
        var factory = Contexts(DatabasePath);
        DatabaseMigrator.ApplyMigrations(factory, DatabasePath);

        using var context = factory.CreateDbContext();
        context.Sessions.Add(new LoggingSession(SessionId, "Legacy"));
        context.SaveChanges();

        if (nullColour)
        {
            RelaxTheColourConstraint(context);
        }

        // Raw SQL rather than EF: DataSample.Color is a non-nullable string, so the model this app
        // ships cannot express the row an older one wrote.
        context.Database.ExecuteSqlRaw(
            """
            INSERT INTO "Samples"
                ("LoggingSessionID", "Value", "TimestampTicks", "DeviceName", "ChannelName", "DeviceSerialNo", "Color", "Type")
            VALUES (@session, 1.25, 638000000000000000, 'Nyquist', 'AI0', 'SN-1', @colour, 0)
            """,
            new SqliteParameter("@session", SessionId),
            new SqliteParameter("@colour", nullColour ? DBNull.Value : string.Empty));

        return factory;
    }

    /// <summary>
    /// Rebuilds <c>Samples</c> the way it stood before the colour was required — same columns, same
    /// key, same foreign key, same indexes, but <c>Color TEXT NULL</c>.
    /// </summary>
    /// <remarks>
    /// A rebuild rather than a <c>writable_schema</c> edit of the stored DDL: the rebuild states the
    /// old shape outright instead of pattern-matching the text EF happened to generate, so it cannot
    /// quietly stop applying and leave the test asserting against the current schema.
    /// </remarks>
    private static void RelaxTheColourConstraint(LoggingContext context)
    {
        context.Database.ExecuteSqlRaw("""DROP TABLE "Samples";""");
        context.Database.ExecuteSqlRaw(
            """
            CREATE TABLE "Samples" (
                "ID" INTEGER NOT NULL CONSTRAINT "PK_Samples" PRIMARY KEY AUTOINCREMENT,
                "LoggingSessionID" INTEGER NOT NULL,
                "Value" REAL NOT NULL,
                "TimestampTicks" INTEGER NOT NULL,
                "DeviceName" TEXT NOT NULL,
                "ChannelName" TEXT NOT NULL,
                "DeviceSerialNo" TEXT NOT NULL,
                "Color" TEXT NULL,
                "Type" INTEGER NOT NULL,
                CONSTRAINT "FK_Samples_Sessions_LoggingSessionID" FOREIGN KEY ("LoggingSessionID")
                    REFERENCES "Sessions" ("ID") ON DELETE CASCADE
            );
            """);
        context.Database.ExecuteSqlRaw(
            """CREATE INDEX "IX_Samples_LoggingSessionID" ON "Samples" ("LoggingSessionID");""");
        context.Database.ExecuteSqlRaw(
            """
            CREATE INDEX "IX_Samples_LoggingSessionID_TimestampTicks"
                ON "Samples" ("LoggingSessionID", "TimestampTicks");
            """);
    }

    /// <summary>
    /// The same SQLite context factory <c>App.Initialize</c> registers in DI, without the container.
    /// </summary>
    /// <remarks>
    /// Kept to one method so it is the single line to reroute when #225 lands
    /// <c>Daqifi.Avalonia.Tests/TestDatabase.cs</c> — which becomes the only file in this project
    /// allowed to name a data source — to <c>TestDatabase.Contexts(databasePath)</c>.
    /// </remarks>
    private static IDbContextFactory<LoggingContext> Contexts(string databasePath) =>
        new TestContextFactory(databasePath);

    private sealed class TestContextFactory(string databasePath) : IDbContextFactory<LoggingContext>
    {
        public LoggingContext CreateDbContext() => new(
            new DbContextOptionsBuilder<LoggingContext>()
                .UseSqlite($"Data source={databasePath}")
                .ConfigureWarnings(w => w.Ignore(RelationalEventId.PendingModelChangesWarning))
                .Options);
    }

    /// <summary>
    /// Swallows the diagnostics so a fixture's noise does not write to the real log. Kept local, like
    /// the suite's two other test loggers: they are not interchangeable (the bootloader one counts
    /// errors), so folding three near-copies into one is its own change and not this one's.
    /// </summary>
    private sealed class SilentLogger : IAppLogger
    {
        public void Information(string message) { }

        public void Warning(string message) { }

        public void Warning(Exception ex, string message) { }

        public void Error(string message) { }

        public void Error(Exception ex, string message) { }

        public void AddBreadcrumb(
            string category,
            string message,
            Daqifi.Desktop.Common.Loggers.BreadcrumbLevel level = Daqifi.Desktop.Common.Loggers.BreadcrumbLevel.Info)
        { }

        public void SetDeviceContext(
            string model, string serialNumber, string firmwareVersion, string connectionType, int activeChannels) { }

        public void ClearDeviceContext() { }

        public void Shutdown() { }
    }

    #endregion
}
