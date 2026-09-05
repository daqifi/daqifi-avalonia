using Daqifi.Avalonia.Tests.ViewModels;
using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Logger;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
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
/// <para>NINE of the eighteen tests in this file fail against the unchanged code, which is the
/// evidence that the defect was real: seven throw <see cref="FormatException"/>, and two come back
/// <c>#00000000</c> — the invisible one — where <c>#FF808080</c> was expected. Of this class's
/// thirteen, seven fail (five throwing, two silently wrong) and six pass because they are meant to:
/// the three good-colour guards, and the three above that characterise today's behaviour rather than
/// the fix's. The other two failures are in <see cref="ChannelColorLivePlotTests"/>. Measured by
/// checking the merge base out, applying only this file to it and running it — not by reasoning
/// about which ones ought to fail. The blank case is the one a <c>?? fallback</c> alone would have
/// left open.</para>
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

    /// <summary>
    /// The other premise worth pinning, because review assumed the opposite: <see cref="OxyColor.Parse"/>
    /// on 2.2.0 understands hex and NOTHING else. It has no named-colour table, so <c>"Red"</c> is
    /// exactly as unusable as <c>"red"</c>.
    /// </summary>
    /// <remarks>
    /// This is what makes case irrelevant to MEANING here — the only thing that parses is hex, and hex
    /// is case-insensitive — and therefore what makes the live plot's raw-string comparison safe either
    /// way. It is pinned rather than assumed because an OxyPlot bump that added named colours would
    /// change the answer without changing a line of this repo.
    /// </remarks>
    [Fact]
    public void A_named_colour_does_not_parse_at_all()
    {
        Assert.Throws<FormatException>(() => OxyColor.Parse("Red"));
        Assert.Throws<FormatException>(() => OxyColor.Parse("red"));
    }

    #endregion

    #region The crash, end to end

    /// <summary>
    /// The user-visible failure, end to end: open a session logged before the colour column was
    /// required. Unfixed, the blank row throws out of series construction and the null row comes back
    /// transparent — one loses the session, the other loses the channel.
    ///
    /// <para>Both halves of the outcome are asserted, not just the absence of a throw: the channel has
    /// to still be THERE, visible, and drawn in a colour a person can see. "No crash" bought by
    /// dropping the series would lose the user's data every bit as quietly as the transparent one did.</para>
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
    /// A database with one session and one sample row whose colour is missing. For the null case it is
    /// shaped like a file written before <c>Samples.Color</c> was required — the current schema
    /// everywhere else, but a <c>Samples</c> table whose <c>Color</c> is nullable. For the empty-string
    /// case the schema is left exactly as the migrations build it, because it does not need relaxing.
    /// </summary>
    /// <param name="nullColour">
    /// <see langword="true"/> stores SQL NULL — only possible in the older table. <see langword="false"/>
    /// stores <c>""</c>, which the CURRENT <c>NOT NULL</c> column accepts perfectly happily, so that
    /// half of the exposure does not even need an old file.
    /// </param>
    /// <remarks>
    /// The contexts come from <see cref="TestDatabase"/>, which is the one place in this project
    /// allowed to name a SQLite data source (#225, enforced by
    /// <c>.github/scripts/check_test_sqlite_pooling.py</c>). Going through it is not only what
    /// satisfies the guard: it is what keeps this fixture's connections UNPOOLED, so the
    /// process-global <c>ClearAllPools()</c> that <see cref="DatabaseMigrator"/> calls on the very
    /// path invoked below cannot dispose a <c>sqlite3</c> handle another test class is mid-query on
    /// (#210). It also carries the pending-model-changes suppression this fixture needs, since
    /// <see cref="RelaxTheColourConstraint"/> moves the schema away from the model snapshot.
    /// </remarks>
    private IDbContextFactory<LoggingContext> SeedLegacySession(bool nullColour)
    {
        var factory = TestDatabase.Contexts(DatabasePath);
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

/// <summary>
/// The same fallback on the LIVE plot, where the cost of getting it wrong is different: the session
/// viewer converts a colour once per channel, but <see cref="PlotLogger.Log(DataSample)"/> runs on the
/// device transport thread for every sample of every channel.
///
/// <para>Review of this PR caught that guarding the conversion was not enough there. The per-sample
/// "has the colour changed?" test compared the sample's string against <c>series.Color.ToString()</c>
/// — raw against canonical — so any value whose two forms differ never compared equal and was
/// re-converted on every sample, with an unusable one throwing and being caught every time. The
/// logger now remembers the string each series was built from and compares raw against raw, which
/// also stops rendering a string per sample just to compare it.</para>
///
/// <para>Two of these five fail against the unchanged code, both by throwing out of
/// <c>AddChannelSeries</c> — so they do pin the live path's share of the crash. What they do NOT
/// prove is the remembering: the thing review flagged is a cost rather than an output, and every
/// test here passes under either comparison. They are guards on what a stale cache would break,
/// not a measurement of how often the conversion runs.</para>
///
/// <para>Separate class, and on the app-host collection, because <c>AddChannelSeries</c> reads
/// <c>LoggingManager.Instance</c> to mirror channel visibility, and that singleton's lazy constructor
/// resolves an <c>IDbContextFactory</c> off <c>App.ServiceProvider</c> — null in a bare test host, so
/// the property throws before its own null check can help. <c>App.InitializeMobile()</c> is the
/// supported way to supply it, already used by <c>DeviceRefusalCrashTests</c>, and it runs against the
/// throwaway <c>DAQIFI_DATA_DIR</c> the assembly's module initializer sets — never the real one.</para>
/// </summary>
[Collection(AppHostCollection.Name)]
public class ChannelColorLivePlotTests
{
    private static readonly OxyColor Fallback = OxyColor.Parse("#FF808080");

    public ChannelColorLivePlotTests() => Daqifi.Desktop.App.InitializeMobile();

    /// <summary>
    /// The case that motivated the change: an unusable colour, arriving sample after sample, has to
    /// settle on the fallback rather than being re-converted — and re-thrown through — each time. What
    /// is asserted is the outcome that would survive either implementation; that it settles is what the
    /// comparison against the remembered raw string buys, and the two tests below hold that honest.
    /// </summary>
    [Fact]
    public void A_repeated_unusable_colour_still_draws_every_sample_in_the_fallback()
    {
        var plotter = new PlotLogger();

        plotter.Log(Sample("not a colour"));
        plotter.Log(Sample("not a colour"));
        plotter.Log(Sample("not a colour"));

        var series = Assert.Single(plotter.LoggedChannels.Values);
        Assert.Equal(Fallback, series.Color);

        // Every sample was still plotted: the fallback must not cost the user data.
        Assert.Equal(3, plotter.LoggedPoints.Values.Single().Count);
    }

    /// <summary>
    /// The guard on remembering anything at all, and the one thing a stale cache would break: a
    /// channel whose colour really does change mid-stream must still repaint. Get this wrong and the
    /// series keeps its first colour forever, which nothing else here would notice.
    /// </summary>
    [Fact]
    public void A_colour_that_changes_mid_stream_still_repaints_the_series()
    {
        var plotter = new PlotLogger();

        plotter.Log(Sample("#FFD32F2F"));
        var series = Assert.Single(plotter.LoggedChannels.Values);
        Assert.Equal(OxyColor.Parse("#FFD32F2F"), series.Color);

        plotter.Log(Sample("#FF1976D2"));

        Assert.Equal(OxyColor.Parse("#FF1976D2"), series.Color);
    }

    /// <summary>
    /// A colour that changes only in case still lands on the right colour. Review raised the opposite
    /// worry — that comparing case-INSENSITIVELY would call <c>"red"</c> and <c>"Red"</c> equal and
    /// strand a series that should have repainted — on the premise that
    /// <see cref="OxyColor.Parse"/> resolves named colours case-sensitively. It does not resolve them
    /// at all on 2.2.0 (see
    /// <see cref="ChannelColorFallbackTests.A_named_colour_does_not_parse_at_all"/>), so no pair of
    /// strings differing only in case can mean two different colours, and neither comparison could
    /// have got this wrong.
    ///
    /// <para>The comparison is <c>Ordinal</c> anyway, and this is what pins it: a case-only change is
    /// then simply a change, costing one conversion and settling, rather than resting on a fact about
    /// a third-party parser that a version bump could quietly reverse.</para>
    /// </summary>
    [Theory]
    [InlineData("#FFD32F2F", "#ffd32f2f")]  // the same colour, spelled two ways: must not drift
    [InlineData("red", "Red")]              // neither is parseable here: the fallback, either way
    public void A_colour_that_changes_only_in_case_lands_on_the_same_colour(string first, string second)
    {
        var plotter = new PlotLogger();

        plotter.Log(Sample(first));
        var series = Assert.Single(plotter.LoggedChannels.Values);
        var afterFirst = series.Color;

        plotter.Log(Sample(second));

        Assert.Equal(afterFirst, series.Color);
    }

    /// <summary>
    /// And the lifecycle half: clearing the plot has to drop the remembered colour along with the
    /// series it belonged to, or the next session's first sample would find its colour already
    /// "current" against a series that no longer exists.
    /// </summary>
    [Fact]
    public void Clearing_the_plot_forgets_the_remembered_colour()
    {
        var plotter = new PlotLogger();

        plotter.Log(Sample("#FFD32F2F"));
        plotter.ClearPlot();
        plotter.Log(Sample("#FFD32F2F"));

        var series = Assert.Single(plotter.LoggedChannels.Values);
        Assert.Equal(OxyColor.Parse("#FFD32F2F"), series.Color);
    }

    /// <summary>One channel's sample, with only the colour varying.</summary>
    private static DataSample Sample(string? colour) => new()
    {
        ChannelName = "AI0",
        DeviceName = "Nyquist",
        DeviceSerialNo = "SN-1",
        Type = ChannelType.Analog,
        Color = colour!,
        Value = 1.25,
        TimestampTicks = 638000000000000000
    };
}
