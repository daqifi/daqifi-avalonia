using Daqifi.Desktop.Channel;
using Daqifi.Desktop.Exporter;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.ViewModels;
using Microsoft.EntityFrameworkCore;
using Xunit;
using ChannelType = Daqifi.Core.Channel.ChannelType;

namespace Daqifi.Avalonia.Tests.Exporter;

/// <summary>
/// Pins the export's most basic promise: the dialog says "Export complete" only when a file was
/// actually written.
///
/// <para>
/// The regression (issue #312): typing <c>0</c> into the dialog's <b>Average Every "N" Samples</b>
/// box and pressing Export produced a green tick, the words "Export complete", the destination path
/// and an <b>Open Folder</b> button — over an empty folder. <c>ExportAverageSamples</c> returned
/// <c>void</c>, so its "window must be positive" rejection was a bare <c>return</c> that the caller
/// could not tell apart from a finished export; <c>ExportDialogViewModel</c>'s <c>finally</c> block
/// set <c>ExportSucceeded = !failed</c>, and nothing had set <c>failed</c>. The only trace was one
/// Warning in the log file.
/// </para>
///
/// <para>
/// The same <c>void</c>-reads-as-success shape had a second instance the ticket names:
/// <c>TryBuildSource</c> returns null for a session with no channels, and both void overloads
/// returned silently on it. So these tests hold BOTH ends of the seam — the exporter reports what it
/// did, and the dialog reports what the exporter told it — rather than special-casing the one input
/// that was reported.
/// </para>
///
/// <para>
/// End-to-end over a real SQLite database and the dialog's own export command, so every assertion
/// about "no file was written" is about the file system, not about a flag.
/// </para>
/// </summary>
public sealed class ExportResultHonestyTests : IDisposable
{
    /// <summary>Throwaway root for this test's database and CSVs. Never the real DAQiFi data
    /// directory (see <see cref="TestDataDirectory"/>).</summary>
    private readonly string _root;
    private readonly IDbContextFactory<LoggingContext> _contexts;
    private int _nextSessionId;

    public ExportResultHonestyTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "daqifi-export-honesty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _contexts = TestDatabase.Contexts(Path.Combine(_root, "DAQiFiDatabase.db"));
        using var context = _contexts.CreateDbContext();
        context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* best effort — a leftover temp directory must not fail a test */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }

    // ──────────────────────────── the reported bug, end to end ────────────────────────────

    /// <summary>
    /// The reported scenario exactly: an averaged export with the window set to <c>0</c> (or a
    /// negative number, which the bare TextBox accepted just as happily). The box is driven as TEXT
    /// because that is what it holds — an emptied box and a box holding letters are the same
    /// "the number on screen cannot be exported by" as <c>"0"</c> is, and against an <c>int</c>
    /// property they were not: the conversion failed, the last good number survived out of sight,
    /// and the export used it. Driven through the command rather than through <c>CanExecute</c>, so
    /// it holds even for a caller that skips the guard the dialog now applies — the user must never
    /// be told an export succeeded when the folder is empty.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("2.5")]
    [InlineData("99999999999999999999")]
    [InlineData(null)]
    public async Task An_averaged_export_with_an_unusable_window_is_not_reported_as_complete(string? box)
    {
        var session = SeedSession("AI0", 1.25, withSamples: true);
        var destination = Path.Combine(_root, "readings.csv");

        var viewModel = new ExportDialogViewModel(_contexts, session.ID)
        {
            ExportFilePath = destination,
            ExportAllSelected = false,
            ExportAverageSelected = true,
            AverageQuantityText = box,
        };

        await viewModel.ExportLoggingSessionsCommand.ExecuteAsync(null);

        Assert.False(File.Exists(destination), $"the export claims to have written a file: {Present()}");
        Assert.False(viewModel.ExportSucceeded,
            $"the dialog reported success over an empty folder: '{viewModel.ExportResultMessage}'");
        Assert.True(viewModel.IsExportComplete, "the dialog must show a result, not sit on the form saying nothing");
    }

    /// <summary>
    /// The second instance of the same shape, and the one that survives the input guard: a session
    /// whose rows are gone (or that never recorded any) has no channels, so
    /// <c>TryBuildSource</c> declines and no CSV is written. That is a legitimate outcome — it is
    /// reporting it as "Export complete" that is not.
    /// </summary>
    [Fact]
    public async Task An_export_of_a_session_with_no_data_is_not_reported_as_complete()
    {
        var session = SeedSession("AI0", 1.25, withSamples: false);
        var destination = Path.Combine(_root, "readings.csv");

        var viewModel = new ExportDialogViewModel(_contexts, session.ID)
        {
            ExportFilePath = destination,
        };

        await viewModel.ExportLoggingSessionsCommand.ExecuteAsync(null);

        Assert.False(File.Exists(destination), $"the export claims to have written a file: {Present()}");
        Assert.False(viewModel.ExportSucceeded,
            $"the dialog reported success over an empty folder: '{viewModel.ExportResultMessage}'");
        Assert.True(viewModel.IsExportComplete);
    }

    /// <summary>
    /// A multi-session export that wrote SOME of its files is not a success either: the dialog's
    /// promise is that N selected sessions produce N files, so the result has to say how many
    /// actually landed instead of rounding up to "Export complete".
    /// </summary>
    [Fact]
    public async Task A_partial_multi_session_export_reports_how_many_sessions_it_wrote()
    {
        var withData = SeedSession("AI0", 1.25, withSamples: true);
        var empty = SeedSession("AI1", 2.5, withSamples: false);
        var destination = Path.Combine(_root, "export");

        var viewModel = new ExportDialogViewModel(_contexts, new[] { withData, empty });
        await viewModel.ExportToDirectoryAsync(destination);

        Assert.False(viewModel.ExportSucceeded,
            $"one of two sessions produced no file, yet the dialog said: '{viewModel.ExportResultMessage}'");
        Assert.Contains("1 of 2", viewModel.ExportResultMessage ?? string.Empty, StringComparison.Ordinal);

        // The session that DID have data still got its file: reporting the shortfall must not turn
        // into refusing to export the sessions that were fine.
        Assert.Single(Directory.GetFiles(destination));
    }

    /// <summary>
    /// Zero sessions is the degenerate end of the same count, and the one a naive
    /// <c>exported &lt; requested</c> misses: <c>0 &lt; 0</c> is false, so an export that was never even
    /// attempted lands on the success branch. Only <c>LoggingSessionListViewModel</c>'s own count
    /// check keeps the dialog from opening this way today — a guard in another class, which is not
    /// what should decide whether this one tells the truth.
    /// </summary>
    [Fact]
    public async Task An_export_with_no_sessions_selected_is_not_reported_as_complete()
    {
        var destination = Path.Combine(_root, "export");
        Directory.CreateDirectory(destination);

        var viewModel = new ExportDialogViewModel(_contexts, Array.Empty<LoggingSession>())
        {
            ExportFilePath = destination,
        };

        await viewModel.ExportLoggingSessionsCommand.ExecuteAsync(null);

        Assert.False(viewModel.ExportSucceeded,
            $"an export of nothing at all reported: '{viewModel.ExportResultMessage}'");
        Assert.Contains("no sessions were selected", viewModel.ExportResultMessage ?? string.Empty,
            StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(destination));
    }

    /// <summary>
    /// The ordinary case, so the fix cannot be "always report a failure": a real averaged export
    /// writes its file and reports the success it actually had.
    /// </summary>
    [Fact]
    public async Task A_completed_averaged_export_is_still_reported_as_complete()
    {
        var session = SeedSession("AI0", 1.25, withSamples: true);
        var destination = Path.Combine(_root, "readings.csv");

        var viewModel = new ExportDialogViewModel(_contexts, session.ID)
        {
            ExportFilePath = destination,
            ExportAllSelected = false,
            ExportAverageSelected = true,
            AverageQuantityText = "2",
        };

        await viewModel.ExportLoggingSessionsCommand.ExecuteAsync(null);

        Assert.True(viewModel.ExportSucceeded, viewModel.ExportResultMessage);
        Assert.Contains("AI0", await File.ReadAllTextAsync(destination), StringComparison.Ordinal);
    }

    // ──────────────────────── the input the user actually typed ────────────────────────

    /// <summary>
    /// Where the failure is now made visible: the export cannot be STARTED unless the box holds a
    /// whole number of 1 or more, so the run that could only do nothing never begins. 1 is the
    /// smallest meaningful window (every sample is its own average) and must stay allowed; an
    /// emptied or half-typed box must not be, which is the case an <c>int</c>-typed property could
    /// not even represent.
    /// </summary>
    [Theory]
    [InlineData("0", false)]
    [InlineData("-1", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("abc", false)]
    [InlineData("2.5", false)]
    [InlineData("1e3", false)]
    [InlineData("99999999999999999999", false)]
    // Null because TextBox.Text is nullable and an emptied box can hand the binding one.
    [InlineData(null, false)]
    [InlineData("1", true)]
    [InlineData("2", true)]
    [InlineData(" 10 ", true)]
    public void The_export_button_is_disabled_unless_the_box_holds_a_usable_window(string? box, bool expected)
    {
        var viewModel = new ExportDialogViewModel(_contexts, 1)
        {
            ExportFilePath = Path.Combine(_root, "readings.csv"),
            ExportAllSelected = false,
            ExportAverageSelected = true,
            AverageQuantityText = box,
        };

        Assert.Equal(expected, viewModel.IsAverageQuantityValid);
        Assert.Equal(expected, viewModel.ExportLoggingSessionsCommand.CanExecute(null));
    }

    /// <summary>
    /// The guard belongs to the averaged export only: the same nonsense left in the box while
    /// <b>All Samples</b> is selected is not read by anything and must not block that export.
    /// </summary>
    [Theory]
    [InlineData("0")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData(null)]
    public void An_unused_average_window_does_not_block_an_all_samples_export(string? box)
    {
        var viewModel = new ExportDialogViewModel(_contexts, 1)
        {
            ExportFilePath = Path.Combine(_root, "readings.csv"),
            AverageQuantityText = box,
        };

        Assert.True(viewModel.IsAverageQuantityValid);
        Assert.True(viewModel.ExportLoggingSessionsCommand.CanExecute(null));
    }

    /// <summary>
    /// The button has to re-enable itself. <c>CanExport</c> is only re-queried when something calls
    /// <c>NotifyCanExecuteChanged</c>, and before this fix only <c>ExportFilePath</c> did — so a
    /// guard that nothing re-evaluated would leave Export dead for the rest of the dialog's life
    /// after one stray keystroke.
    /// </summary>
    [Fact]
    public void Correcting_the_average_window_re_enables_the_export_button()
    {
        var viewModel = new ExportDialogViewModel(_contexts, 1)
        {
            ExportFilePath = Path.Combine(_root, "readings.csv"),
            ExportAllSelected = false,
            ExportAverageSelected = true,
        };

        var raised = 0;
        viewModel.ExportLoggingSessionsCommand.CanExecuteChanged += (_, _) => raised++;

        // The keystrokes a user actually makes going from "2" to "10": the box is empty for a
        // moment, then holds "1", and Export has to follow all the way through.
        viewModel.AverageQuantityText = string.Empty;
        Assert.False(viewModel.ExportLoggingSessionsCommand.CanExecute(null));

        viewModel.AverageQuantityText = "0";
        Assert.False(viewModel.ExportLoggingSessionsCommand.CanExecute(null));

        viewModel.AverageQuantityText = "10";
        Assert.True(viewModel.ExportLoggingSessionsCommand.CanExecute(null));

        // Switching export type re-queries too: the same 0 becomes harmless under All Samples.
        viewModel.AverageQuantityText = "0";
        viewModel.ExportAverageSelected = false;
        viewModel.ExportAllSelected = true;
        Assert.True(viewModel.ExportLoggingSessionsCommand.CanExecute(null));

        Assert.True(raised > 0, "nothing told the Export button to re-query its CanExecute");
    }

    // ──────────────────────────── the exporter's own contract ────────────────────────────

    /// <summary>
    /// The exporter no longer has a branch that quietly does nothing. A non-positive window is a
    /// caller error — the dialog cannot produce one any more — so it is refused where a caller
    /// cannot miss it, instead of returning as though the file had been written.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    public void The_exporter_refuses_a_non_positive_average_window(int window)
    {
        var session = SeedSession("AI0", 1.25, withSamples: true);
        var destination = Path.Combine(_root, "readings.csv");

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new OptimizedLoggingSessionExporter(_contexts).ExportAverageSamples(
                session, destination, window, exportRelativeTime: false,
                new Progress<int>(), CancellationToken.None, 0, 1));

        Assert.False(File.Exists(destination), $"the refused export still wrote something: {Present()}");
    }

    /// <summary>
    /// The other half of the seam: when there is genuinely nothing to write, both export entry
    /// points say so in their return value rather than by being indistinguishable from success.
    /// </summary>
    [Fact]
    public void Both_export_entry_points_report_that_they_wrote_nothing()
    {
        var session = SeedSession("AI0", 1.25, withSamples: false);
        var exporter = new OptimizedLoggingSessionExporter(_contexts);

        Assert.False(exporter.ExportLoggingSession(
            session, Path.Combine(_root, "all.csv"), exportRelativeTime: false,
            new Progress<int>(), CancellationToken.None, 0, 1));

        Assert.False(exporter.ExportAverageSamples(
            session, Path.Combine(_root, "avg.csv"), 2, exportRelativeTime: false,
            new Progress<int>(), CancellationToken.None, 0, 1));

        Assert.Empty(Directory.GetFiles(_root, "*.csv"));
    }

    /// <summary>And report the truth the other way round when they did write the file.</summary>
    [Fact]
    public void Both_export_entry_points_report_a_file_they_wrote()
    {
        var session = SeedSession("AI0", 1.25, withSamples: true);
        var exporter = new OptimizedLoggingSessionExporter(_contexts);

        Assert.True(exporter.ExportLoggingSession(
            session, Path.Combine(_root, "all.csv"), exportRelativeTime: false,
            new Progress<int>(), CancellationToken.None, 0, 1));

        Assert.True(exporter.ExportAverageSamples(
            session, Path.Combine(_root, "avg.csv"), 2, exportRelativeTime: false,
            new Progress<int>(), CancellationToken.None, 0, 1));

        Assert.Equal(2, Directory.GetFiles(_root, "*.csv").Length);
    }

    // ──────────────────────────────── the markup half ────────────────────────────────

    /// <summary>
    /// <c>ExportDialog.axaml</c> declares no <c>x:DataType</c>, so both halves of this fix resolve by
    /// reflection: the box the user types in, and the message saying why Export is greyed out. A
    /// rename would break either silently — a disabled button with no reason beside it is the exact
    /// "the app won't say why" failure this fix exists to remove, and a dead box would be worse.
    /// Asserted in pairs, per <see cref="BindingFacts"/>.
    /// </summary>
    [Fact]
    public void The_dialog_binds_the_average_box_and_the_reason_the_export_is_disabled()
    {
        const string view = "Daqifi.Avalonia/Daqifi.Desktop/View/ExportDialog.axaml";

        BindingFacts.AssertBinds(view, "Text=\"{Binding AverageQuantityText, Mode=TwoWay}\"");
        BindingFacts.AssertExposes(typeof(ExportDialogViewModel), nameof(ExportDialogViewModel.AverageQuantityText));

        BindingFacts.AssertBinds(view, "IsVisible=\"{Binding !IsAverageQuantityValid}\"");
        BindingFacts.AssertExposes(typeof(ExportDialogViewModel), nameof(ExportDialogViewModel.IsAverageQuantityValid));
    }

    // ──────────────────────────────────── helpers ────────────────────────────────────

    /// <summary>What is actually sitting in the export folder, for an assertion message that says why.</summary>
    private string Present() =>
        "[" + string.Join(", ", Directory.GetFiles(_root).Select(Path.GetFileName)) + "]";

    /// <summary>
    /// Writes one session into the temp database and returns the detached row the dialog would hand
    /// the exporter. With <paramref name="withSamples"/> false the session row exists but has no
    /// samples at all — the "no channels" case <c>TryBuildSource</c> declines.
    /// </summary>
    private LoggingSession SeedSession(string channelName, double value, bool withSamples)
    {
        // Session ids are assigned by the app, not by SQLite (Sessions.ID has no autoincrement
        // annotation), so the seeder has to supply its own.
        var id = ++_nextSessionId;
        using var context = _contexts.CreateDbContext();
        var start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var session = new LoggingSession { ID = id, Name = "Honesty " + id, SessionStart = start };
        context.Sessions.Add(session);
        context.SaveChanges();

        if (withSamples)
        {
            // Two samples so the averaged export has a full window to fold.
            for (var i = 0; i < 2; i++)
            {
                context.Samples.Add(new DataSample
                {
                    LoggingSession = session,
                    LoggingSessionID = session.ID,
                    DeviceName = "Nyquist",
                    DeviceSerialNo = "SERIAL-1",
                    ChannelName = channelName,
                    Color = "#FFD32F2F",
                    Type = ChannelType.Analog,
                    TimestampTicks = start.AddSeconds(i).Ticks,
                    Value = value + i,
                });
            }

            context.SaveChanges();
        }

        return new LoggingSession { ID = session.ID, Name = session.Name };
    }
}
