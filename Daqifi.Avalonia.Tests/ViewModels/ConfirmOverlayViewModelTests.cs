using Daqifi.Desktop.ViewModels;
using Xunit;

namespace Daqifi.Avalonia.Tests.ViewModels;

/// <summary>
/// Pins <see cref="ConfirmOverlayViewModel"/>, the only thing standing between the user and a
/// permanent delete. Every "Delete session?" / "Delete all sessions?" prompt on both heads awaits
/// <see cref="ConfirmOverlayViewModel.ShowAsync"/> and deletes on <c>true</c> — the desktop list
/// through <c>ILoggingSessionListHost.ShowConfirmAsync</c>, the mobile Storage tab directly — and
/// the Profiles pane awaits it before switching the active profile.
///
/// <para>
/// So the property that matters is that the task resolves <c>true</c> ONLY from the affirmative
/// button, and every other way the prompt can end — cancel, scrim, a host reset, being replaced
/// by another prompt — resolves <c>false</c>. A path that resolved <c>true</c> by accident would
/// delete the user's logged data with nothing on screen having asked for it.
/// </para>
///
/// <para>
/// Characterization, not regression: these pass against the class as it stands. Each was
/// mutation-checked against a one-line edit to the class (see the PR body for the list).
/// </para>
/// </summary>
public sealed class ConfirmOverlayViewModelTests
{
    [Fact]
    public void Showing_a_prompt_opens_it_with_the_callers_content_and_leaves_it_pending()
    {
        var overlay = new ConfirmOverlayViewModel();

        var answer = overlay.ShowAsync("Delete session?", "This can't be undone.", "DELETE", isDestructive: true);

        Assert.True(overlay.IsOpen);
        Assert.Equal("Delete session?", overlay.Title);
        Assert.Equal("This can't be undone.", overlay.Message);
        Assert.Equal("DELETE", overlay.AffirmativeLabel);
        Assert.True(overlay.AffirmativeIsDestructive);
        Assert.False(answer.IsCompleted);
    }

    [Fact]
    public async Task The_affirmative_button_answers_yes_and_closes()
    {
        var overlay = new ConfirmOverlayViewModel();
        var answer = overlay.ShowAsync("Delete session?", "m", "DELETE", isDestructive: true);

        overlay.AffirmativeCommand.Execute(null);

        Assert.True(await answer);
        Assert.False(overlay.IsOpen);
    }

    [Fact]
    public async Task The_negative_button_answers_no_and_closes()
    {
        var overlay = new ConfirmOverlayViewModel();
        var answer = overlay.ShowAsync("Delete session?", "m", "DELETE", isDestructive: true);

        overlay.NegativeCommand.Execute(null);

        Assert.False(await answer);
        Assert.False(overlay.IsOpen);
    }

    /// <summary>
    /// The host-reset path: pane navigation and view unload call <see cref="ConfirmOverlayViewModel.Cancel"/>
    /// so an in-flight prompt is not stranded. It must unwind as a "no" — the user walked away
    /// from the question, they did not agree to it.
    /// </summary>
    [Fact]
    public async Task A_host_cancel_answers_no_and_closes()
    {
        var overlay = new ConfirmOverlayViewModel();
        var answer = overlay.ShowAsync("Delete all sessions?", "m", "DELETE ALL", isDestructive: true);

        overlay.Cancel();

        Assert.False(await answer);
        Assert.False(overlay.IsOpen);
    }

    /// <summary>
    /// A second prompt opened over a pending one replaces it, and the first caller is told "no".
    /// The overlay now shows the second question, so the first caller's pending delete must not
    /// be able to proceed on an answer the user gave to something else.
    /// </summary>
    [Fact]
    public async Task A_new_prompt_answers_the_one_it_replaces_with_no_and_takes_over_the_overlay()
    {
        var overlay = new ConfirmOverlayViewModel();
        var first = overlay.ShowAsync("Delete session?", "first", "DELETE", isDestructive: true);

        var second = overlay.ShowAsync("Switch profile?", "second", "SWITCH");

        Assert.False(await first);
        Assert.True(overlay.IsOpen);
        Assert.Equal("Switch profile?", overlay.Title);
        Assert.Equal("second", overlay.Message);
        Assert.False(second.IsCompleted);

        // And the user's answer now goes to the prompt they can actually see.
        overlay.AffirmativeCommand.Execute(null);
        Assert.True(await second);
    }

    /// <summary>
    /// The buttons and the host reset are all safe when nothing is pending — a double-click on
    /// DELETE, or a view unloading with no prompt open — and a stray press cannot pre-answer the
    /// NEXT prompt.
    /// </summary>
    [Fact]
    public async Task Presses_with_nothing_pending_do_nothing_and_do_not_answer_the_next_prompt()
    {
        var overlay = new ConfirmOverlayViewModel();

        overlay.AffirmativeCommand.Execute(null);
        overlay.NegativeCommand.Execute(null);
        overlay.Cancel();
        Assert.False(overlay.IsOpen);

        var answer = overlay.ShowAsync("Delete session?", "m", "DELETE", isDestructive: true);
        Assert.False(answer.IsCompleted);

        overlay.AffirmativeCommand.Execute(null);
        overlay.AffirmativeCommand.Execute(null); // the double-click's second half
        Assert.True(await answer);
        Assert.False(overlay.IsOpen);
    }

    /// <summary>
    /// Every call sets every piece of content, so a prompt shown with the defaults after a
    /// destructive one does not inherit its red DELETE button.
    /// </summary>
    [Fact]
    public void A_default_prompt_after_a_destructive_one_does_not_inherit_its_label_or_danger_style()
    {
        var overlay = new ConfirmOverlayViewModel();
        _ = overlay.ShowAsync("Delete all sessions?", "m", "DELETE ALL", isDestructive: true);
        overlay.Cancel();

        _ = overlay.ShowAsync("Switch profile?", "m2");

        Assert.Equal("OK", overlay.AffirmativeLabel);
        Assert.False(overlay.AffirmativeIsDestructive);
    }

    /// <summary>
    /// The awaiter must not resume INSIDE the button's command handler. The task is created with
    /// <see cref="TaskCreationOptions.RunContinuationsAsynchronously"/> for exactly that reason (the
    /// class comment names re-entrancy on the UI thread), so even a continuation that asks to run
    /// synchronously is not run on the stack of <c>Execute</c>.
    ///
    /// <para>
    /// Deterministic without any wait: the test thread is busy inside <c>Execute</c> until it
    /// returns, so a continuation that observes itself on the test thread with <c>Execute</c> not
    /// yet returned can only have been run inline.
    /// </para>
    /// </summary>
    [Fact]
    public async Task The_awaiter_does_not_resume_inside_the_button_handler()
    {
        var overlay = new ConfirmOverlayViewModel();
        var answer = overlay.ShowAsync("Delete session?", "m", "DELETE", isDestructive: true);

        var testThread = Environment.CurrentManagedThreadId;
        var executeReturned = false;
        var ranInline = answer.ContinueWith(
            _ => Environment.CurrentManagedThreadId == testThread && !Volatile.Read(ref executeReturned),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        overlay.AffirmativeCommand.Execute(null);
        Volatile.Write(ref executeReturned, true);

        Assert.False(await ranInline);
        Assert.True(await answer);
    }
}
