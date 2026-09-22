using System.Collections.ObjectModel;
using Daqifi.Desktop.Logger;
using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// What the Logged Data pane says when deleting ONE session fails (issue #434).
///
/// <para>The user picks a session, clicks Delete, and confirms a red DELETE dialog. The pane says
/// <c>Deleting Logging Session #N</c>, the spinner clears — and the row is still there. Keeping the
/// row is correct (#592: the delete rethrows, so the data is still on disk and the list is right to
/// go on showing it), but nothing was said, so the user cannot tell a refused delete from a stale
/// list and clicks Delete again.</para>
///
/// <para><see cref="LoggingSessionListViewModel.DeleteAllSessionsAsync"/>, in the same class, already
/// states the rule: a delete that fails "keeps the session list <em>and tells the user</em>, instead
/// of … leaving a log line as the only trace (#183)". These rows hold the single-session path to the
/// same rule — and, because the sibling holds its dialog over the busy flag's <c>finally</c> so it is
/// not shown behind a spinner that never clears, they pin that ordering too.</para>
/// </summary>
public class DeleteSessionFailureTests
{
    /// <summary>
    /// The defect. <c>SessionDataRepository.DeleteSession</c> rolls back and rethrows on failure
    /// (a locked database, a read-only data directory, a full disk), so this catch is a live path
    /// and not a theoretical one.
    /// </summary>
    [Fact]
    public async Task DeleteSession_WhenTheDatabaseDeleteFails_KeepsTheRowAndTellsTheUser()
    {
        var session = new LoggingSession(7, "Session_7");
        var host = new RecordingHost(session)
        {
            DeleteFailure = new IOException("database is locked"),
        };
        var listViewModel = NewListViewModel(host);

        await listViewModel.DeleteSessionAsync(session);

        // #592: the data is still on disk, so the row stays.
        Assert.Same(session, Assert.Single(host.LoggingSessions));

        // #434: and the user is told, in the same register as the delete-all failure.
        var shown = Assert.Single(host.MessagesShown);
        Assert.Equal("Delete Failed", shown.Title);
        Assert.Contains("could not be deleted", shown.Message);
    }

    /// <summary>
    /// The convention's other half, taken verbatim from <c>DeleteAllSessionsAsync</c>: the dialog is
    /// held over the busy flag's <c>finally</c>, so the pane has stopped saying it is deleting before
    /// the message appears. A modal shown from behind the spinner leaves the spinner up underneath it.
    /// </summary>
    [Fact]
    public async Task DeleteSession_WhenTheDeleteFails_ClearsTheSpinnerBeforeShowingTheMessage()
    {
        var session = new LoggingSession(7, "Session_7");
        var host = new RecordingHost(session)
        {
            DeleteFailure = new IOException("database is locked"),
        };
        var listViewModel = NewListViewModel(host);

        await listViewModel.DeleteSessionAsync(session);

        var shown = Assert.Single(host.MessagesShown);
        Assert.False(shown.WasBusy);
        Assert.Equal(string.Empty, shown.BusyReason);
    }

    /// <summary>
    /// The outer catch. Everything before the database call is covered by it too — the confirm
    /// dialog itself, the host's selection write — and from the user's side that is the identical
    /// experience: a destructive action confirmed, a row still on screen, nothing said.
    /// </summary>
    [Fact]
    public async Task DeleteSession_WhenTheConfirmDialogThrows_KeepsTheRowAndTellsTheUser()
    {
        var session = new LoggingSession(7, "Session_7");
        var host = new RecordingHost(session)
        {
            ConfirmFailure = new InvalidOperationException("no window to host the dialog"),
        };
        var listViewModel = NewListViewModel(host);

        await listViewModel.DeleteSessionAsync(session);

        Assert.Same(session, Assert.Single(host.LoggingSessions));
        Assert.Equal("Delete Failed", Assert.Single(host.MessagesShown).Title);
    }

    /// <summary>
    /// The control that stops the message becoming noise: a delete that worked removes the row and
    /// says nothing. (On its own this asserts a default — <c>MessagesShown</c> is empty when the
    /// dialog seam is broken entirely — which is why the failure rows above carry the weight.)
    /// </summary>
    [Fact]
    public async Task DeleteSession_OnTheHappyPath_RemovesTheRowAndSaysNothing()
    {
        var session = new LoggingSession(7, "Session_7");
        var host = new RecordingHost(session);
        var listViewModel = NewListViewModel(host);

        await listViewModel.DeleteSessionAsync(session);

        Assert.Empty(host.LoggingSessions);
        Assert.Empty(host.MessagesShown);
        Assert.Equal(1, host.NotifyCount);
    }

    /// <summary>
    /// Declining the confirmation is not a failure: nothing is deleted, and the user is not told
    /// about a delete they just cancelled.
    /// </summary>
    [Fact]
    public async Task DeleteSession_WhenTheUserDeclinesTheConfirmation_DeletesNothingAndSaysNothing()
    {
        var session = new LoggingSession(7, "Session_7");
        var host = new RecordingHost(session) { ConfirmAnswer = false };
        var listViewModel = NewListViewModel(host);

        await listViewModel.DeleteSessionAsync(session);

        Assert.Equal(0, host.DeleteAttempts);
        Assert.Same(session, Assert.Single(host.LoggingSessions));
        Assert.Empty(host.MessagesShown);
    }

    /// <summary>
    /// A dialog seam that is itself broken must not take the command down with it — the same
    /// reasoning <c>DeleteAllSessionsAsync</c> records for its own guarded show (#183 is a crash).
    /// </summary>
    [Fact]
    public async Task DeleteSession_WhenTheFailureDialogAlsoFails_DoesNotPropagate()
    {
        var session = new LoggingSession(7, "Session_7");
        var host = new RecordingHost(session)
        {
            DeleteFailure = new IOException("database is locked"),
            MessageFailure = new InvalidOperationException("no window to host the dialog"),
        };
        var listViewModel = NewListViewModel(host);

        await listViewModel.DeleteSessionAsync(session);

        Assert.Same(session, Assert.Single(host.LoggingSessions));
    }

    /// <summary>
    /// The single-session path never touches the logging context or the database path — only the
    /// delete-all purge does — so both are supplied as tripwires rather than as working values.
    /// </summary>
    private static LoggingSessionListViewModel NewListViewModel(RecordingHost host) =>
        new(
            host,
            () => throw new InvalidOperationException("the single-session delete must not resolve the logging context factory"),
            Path.Combine(Path.GetTempPath(), "daqifi-avalonia-tests", "delete-session-434", "DAQiFiDatabase.db"),
            new RecordingAppLogger());

    /// <summary>What the view model showed the user, and what the pane looked like at that moment.</summary>
    private readonly record struct ShownMessage(string Title, string Message, bool WasBusy, string BusyReason);

    /// <summary>
    /// The host seam, recording the dialogs and able to fail the delete. Deliberately separate from
    /// <c>DeleteAllSessionsRecoveryTests</c>'s host: that one drives a real purge against a real
    /// database and cannot fail a single-session delete, which is the whole subject here.
    /// </summary>
    private sealed class RecordingHost : ILoggingSessionListHost
    {
        private bool _isBusy;
        private string _busyReason = string.Empty;

        internal RecordingHost(params LoggingSession[] sessions) =>
            LoggingSessions = new ObservableCollection<LoggingSession>(sessions);

        /// <summary>Thrown by <see cref="DeleteSessionFromDatabase"/> when set, as the repository does.</summary>
        internal Exception? DeleteFailure { get; init; }

        /// <summary>Thrown by <see cref="ShowConfirmAsync"/> when set, to reach the outer catch.</summary>
        internal Exception? ConfirmFailure { get; init; }

        /// <summary>Thrown by <see cref="ShowMessageAsync"/> when set, to break the report itself.</summary>
        internal Exception? MessageFailure { get; init; }

        /// <summary>What the confirmation answers when it does not throw.</summary>
        internal bool ConfirmAnswer { get; init; } = true;

        internal List<ShownMessage> MessagesShown { get; } = [];

        internal int DeleteAttempts { get; private set; }

        internal int NotifyCount { get; private set; }

        public ObservableCollection<LoggingSession> LoggingSessions { get; }

        public LoggingSession SelectedLoggingSession { set { } }

        public bool IsLoggedDataBusy { set => _isBusy = value; }

        public string LoggedDataBusyReason { set => _busyReason = value; }

        public bool IsLoggingActive => false;

        public void NotifyLoggingSessionsChanged() => NotifyCount++;

        public void DisplaySessionOnPlot(LoggingSession session) { }

        public void DeleteSessionFromDatabase(LoggingSession session)
        {
            DeleteAttempts++;
            if (DeleteFailure is { } failure)
            {
                throw failure;
            }
        }

        public void ClearPlot() { }

        public void SuspendConsumer() { }

        public void ResumeConsumer() { }

        public void ClearBuffer() { }

        public void DiscardPendingBatch() { }

        public Task ShowExportDialogForSessionAsync(int sessionId) => Task.CompletedTask;

        public Task ShowExportDialogForSessionsAsync(IReadOnlyList<LoggingSession> sessions) => Task.CompletedTask;

        public Task<bool> ShowConfirmAsync(string title, string message, string affirmativeLabel, bool isDestructive)
        {
            if (ConfirmFailure is { } failure)
            {
                throw failure;
            }

            return Task.FromResult(ConfirmAnswer);
        }

        public Task ShowMessageAsync(string title, string message)
        {
            // Captured with the busy state as the view model left it, so the ordering is testable.
            MessagesShown.Add(new ShownMessage(title, message, _isBusy, _busyReason));

            if (MessageFailure is { } failure)
            {
                throw failure;
            }

            return Task.CompletedTask;
        }
    }
}
