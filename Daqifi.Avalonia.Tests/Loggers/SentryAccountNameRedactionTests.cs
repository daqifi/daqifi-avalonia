using System.Text;
using System.Text.Json;
using Daqifi.Desktop.Common.Loggers;
using Sentry;
using Sentry.Extensibility;
using Sentry.Protocol;
using Sentry.Protocol.Envelopes;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// An error event must not carry the user's account name (issue #366).
///
/// <para><b>Why these tests go through a real client rather than calling the redactor.</b>
/// <c>AppLogger.RedactAccountNames</c> was already correct before this change, and a unit test of
/// it in isolation passed before the change and passes after it. The defect was that nothing on
/// the event path ever called it — it was wired to <c>LeaveBreadcrumb</c> and to nothing else. So
/// every test here assembles an event the way the SDK does, pushes it through
/// <see cref="AppLogger.ConfigureSentryOptions"/> — the configuration the app actually ships —
/// and reads the fields back out of the serialised envelope, which is the exact byte sequence
/// that would go on the wire.</para>
///
/// <para><b>Nothing leaves the machine.</b> The DSN is syntactically valid and points at an
/// unroutable <c>.invalid</c> host, and <see cref="RecordingWorker"/> replaces the SDK's
/// background worker, so envelopes are recorded in memory instead of being handed to any
/// transport. No HTTP client is constructed and no envelope cache is written.</para>
///
/// <para><b>Against #366's merge base</b> this file did not compile: every test but
/// <see cref="Unhooked_options_leak_the_account_name"/> names <c>ConfigureSentryOptions</c> or
/// <c>ScrubAccountNames</c>, neither of which existed there. That control is the one that COULD
/// run there, and it is the positive control for the whole file — it captures through options with
/// no hooks, which is what that base configured, and asserts the account name DOES reach the
/// envelope. It fails if this harness ever stops being able to see the leak.</para>
///
/// <para><b>#369 extends it</b> to the three fields the hook scrubs but nothing pinned — the
/// <c>logentry</c>, breadcrumb <c>data</c> and <c>server_name</c> regions below — and to
/// <c>logentry.params</c>, which was passing through verbatim.</para>
/// </summary>
public class SentryAccountNameRedactionTests
{
    /// <summary>
    /// Syntactically valid, deliberately unroutable, and never reached — see the class remarks.
    /// Matches the shape the #366 reporter used to reproduce the leak off the live project.
    /// </summary>
    private const string InertDsn = "https://0123456789abcdef0123456789abcdef@o0.ingest.us.sentry.invalid/1";

    /// <summary>
    /// An account name no word in any of these messages contains, so a bare "is it present?"
    /// assertion cannot pass or fail by accident. The over-redaction case below deliberately uses
    /// a different, word-like one instead.
    /// </summary>
    private const string Account = "octocat";

    private static readonly string ProfilePath =
        $"/Users/{Account}/Library/Application Support/DAQiFi/DAQifiProfilesConfiguration.xml";

    /// <summary>
    /// The half of the path that has to SURVIVE: which directory and which file is what explains
    /// a failure. A redaction that took the whole path would close the leak and destroy the report.
    /// </summary>
    private const string DiagnosticTail = "Library/Application Support/DAQiFi/DAQifiProfilesConfiguration.xml";

    #region The two halves of #366

    /// <summary>
    /// Half one: the message this app writes. <c>ProfileXmlStore</c> interpolates the profiles
    /// path into it, and <c>Error(Exception, string)</c> puts that message in <c>extra</c>.
    /// </summary>
    [Fact]
    public void A_path_in_our_own_message_is_redacted()
    {
        var captured = CaptureAsErrorWould(
            new InvalidOperationException("no path here"),
            $"Reading the profiles file failed: {ProfilePath}");

        Assert.Equal(
            $"Reading the profiles file failed: /Users/<account>/{DiagnosticTail}",
            captured.Extra("message"));
    }

    /// <summary>
    /// Half two, and the half that rules out a per-call-site fix: .NET builds
    /// <see cref="UnauthorizedAccessException"/>'s own text from the path, so the account name
    /// ships even when OUR message contains no path at all. This is #366's run 2, second event,
    /// reproduced — there the message was the path-free "Error Setting Selected profile".
    /// </summary>
    [Fact]
    public void A_path_only_inside_the_exception_message_is_redacted()
    {
        var captured = CaptureAsErrorWould(
            new UnauthorizedAccessException($"Access to the path '{ProfilePath}' is denied."),
            "Error Setting Selected profile");

        Assert.Equal(
            $"Access to the path '/Users/<account>/{DiagnosticTail}' is denied.",
            Assert.Single(captured.ExceptionValues));
        Assert.DoesNotContain(Account, captured.Raw, StringComparison.Ordinal);
    }

    #endregion

    #region The rest of the event

    /// <summary>
    /// A stack frame's file name is the source path as the compiler saw it, which on a local
    /// build is the building account's home directory. Built by hand rather than thrown, so the
    /// test does not depend on a portable PDB being present — <c>SentryExceptions</c> is only
    /// overwritten by the exception processor when the event carries a CLR exception, so a
    /// hand-built one survives to the hook unchanged.
    /// </summary>
    [Fact]
    public void A_path_in_a_stack_frame_is_redacted()
    {
        var frame = new SentryStackFrame
        {
            FileName = $"/Users/{Account}/projects/daqifi-avalonia/Loggers/ProfileXmlStore.cs",
            AbsolutePath = $"/Users/{Account}/projects/daqifi-avalonia/Loggers/ProfileXmlStore.cs"
        };
        var stacktrace = new SentryStackTrace();
        stacktrace.Frames.Add(frame);

        var @event = new SentryEvent
        {
            SentryExceptions = [new SentryException { Value = "boom", Stacktrace = stacktrace }]
        };

        var captured = Capture(@event, Shipped());

        var expected = "/Users/<account>/projects/daqifi-avalonia/Loggers/ProfileXmlStore.cs";
        Assert.Equal([expected], captured.StackFrameFileNames);
        Assert.DoesNotContain(Account, captured.Raw, StringComparison.Ordinal);
    }

    /// <summary>
    /// A breadcrumb cannot be fixed by the <c>BeforeSend</c> hook — <c>IEventLike.Breadcrumbs</c>
    /// is read-only, so by the time an event carries one it is frozen. The <c>BeforeBreadcrumb</c>
    /// hook catches it a step earlier, which is also why the redaction could come OUT of
    /// <c>LeaveBreadcrumb</c>: the hook covers that path and every other one.
    /// </summary>
    [Fact]
    public void A_path_in_a_breadcrumb_is_redacted()
    {
        var options = Shipped();
        var scope = new Scope(options);
        scope.AddBreadcrumb($"Saving the graph image to '{ProfilePath}' was blocked", "log");

        var captured = Capture(new SentryEvent(new InvalidOperationException("boom")), options, scope);

        Assert.Equal(
            $"Saving the graph image to '/Users/<account>/{DiagnosticTail}' was blocked",
            Assert.Single(captured.BreadcrumbMessages));
    }

    #endregion

    #region Scrubbed by #367, pinned by #369

    /// <summary>
    /// The <c>logentry</c> interface — what <c>CaptureMessage</c> builds. Both halves carry a path
    /// here so that unwiring either line of the rebuild is caught: the template is what a plain
    /// <c>CaptureMessage(text)</c> puts in <c>message</c>, and <c>formatted</c> is the rendered
    /// form a structured-logging integration would add beside it.
    /// </summary>
    [Fact]
    public void A_path_in_a_log_message_is_redacted()
    {
        var @event = new SentryEvent
        {
            Message = new SentryMessage
            {
                Message = $"Reading the profiles file failed: {ProfilePath}",
                Formatted = $"Reading the profiles file failed: {ProfilePath} (errno 13)"
            }
        };

        var captured = Capture(@event, Shipped());

        Assert.Equal(
            $"Reading the profiles file failed: /Users/<account>/{DiagnosticTail}",
            captured.LogEntry("message"));
        Assert.Equal(
            $"Reading the profiles file failed: /Users/<account>/{DiagnosticTail} (errno 13)",
            captured.LogEntry("formatted"));
        Assert.DoesNotContain(Account, captured.Raw, StringComparison.Ordinal);
    }

    /// <summary>
    /// A breadcrumb's <c>data</c> values, which the SDK populates on the breadcrumbs it writes
    /// itself. The message here deliberately contains no path, so this also pins the rebuild
    /// branch in the <c>Breadcrumb</c> overload: a breadcrumb whose message is unchanged but whose
    /// data is not must still be replaced rather than returned as it arrived.
    /// </summary>
    [Fact]
    public void A_path_in_breadcrumb_data_is_redacted()
    {
        var options = Shipped();
        var scope = new Scope(options);
        scope.AddBreadcrumb(
            "Loading the selected profile failed",
            "log",
            null,
            new Dictionary<string, string> { ["path"] = ProfilePath });

        var captured = Capture(new SentryEvent(new InvalidOperationException("boom")), options, scope);

        Assert.Equal(
            $"/Users/<account>/{DiagnosticTail}",
            Assert.Single(captured.BreadcrumbData("path")));
        Assert.DoesNotContain(Account, captured.Raw, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>server_name</c>, which on a developer machine is whatever the host is called and on a
    /// build agent can be a path. Two assertions, because the field has two states worth pinning:
    /// the shipped options never populate it at all (<c>SendDefaultPii = false</c>, and the SDK
    /// only fills it in from the machine name when that is on), and an event that carries one
    /// anyway has it scrubbed. Without the second half the scrub line is unpinned; without the
    /// first, the belt-and-braces claim in <c>ScrubAccountNames</c>'s remarks is unpinned.
    /// </summary>
    [Fact]
    public void A_path_in_the_server_name_is_redacted_and_the_field_is_absent_by_default()
    {
        var captured = Capture(new SentryEvent { ServerName = $"/Users/{Account}/build-agent" }, Shipped());

        Assert.Equal("/Users/<account>/build-agent", captured.ServerName);
        Assert.DoesNotContain(Account, captured.Raw, StringComparison.Ordinal);

        var untouched = Capture(new SentryEvent(new InvalidOperationException("boom")), Shipped());

        Assert.False(untouched.Has("server_name"));
    }

    #endregion

    #region logentry.params (#369)

    /// <summary>
    /// The one field of <c>logentry</c> the #367 rebuild passed through verbatim. Not reachable
    /// from this app today — nothing calls <c>CaptureMessage</c>, so no event carries a
    /// <c>logentry</c> at all — so this is the redactor being put on a path before anything walks
    /// it, which is the same shape as #366 caught one step earlier.
    /// </summary>
    [Fact]
    public void A_path_in_a_log_message_parameter_is_redacted()
    {
        var @event = new SentryEvent
        {
            Message = new SentryMessage
            {
                Message = "Reading the profiles file failed: {0}",
                Formatted = "Reading the profiles file failed: <path>",
                Params = [ProfilePath]
            }
        };

        var captured = Capture(@event, Shipped());

        Assert.Equal(
            $"/Users/<account>/{DiagnosticTail}",
            Assert.Single(captured.MessageParams).GetString());
        Assert.DoesNotContain(Account, captured.Raw, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Params</c> is a collection of arbitrary objects, not of strings, and only its string
    /// elements are redacted. Stringifying the rest to run them past the redactor would rewrite
    /// the serialised types of every parameter list in the app — a number arriving as <c>"42"</c>
    /// instead of <c>42</c> — which is a change to the payload well beyond the scrub. This pins
    /// that: each element keeps the JSON kind it would have had, and the elements the redactor
    /// cannot see through — a null, a nested collection — are passed on as they arrived.
    /// </summary>
    [Fact]
    public void Log_message_parameters_that_are_not_strings_keep_their_serialised_type()
    {
        var @event = new SentryEvent
        {
            Message = new SentryMessage
            {
                Message = "{0} {1} {2} {3} {4}",
                Params = [ProfilePath, 42, true, null!, new[] { "AI0", "AI1" }]
            }
        };

        var kinds = Capture(@event, Shipped()).MessageParams.Select(value => value.ValueKind);

        Assert.Equal(
            [JsonValueKind.String, JsonValueKind.Number, JsonValueKind.True, JsonValueKind.Null, JsonValueKind.Array],
            kinds);
    }

    /// <summary>
    /// The adjacency cases, on the parameter path this time. Over-redaction is the failure mode of
    /// this class of change — #367 shipped one, where a profile of <c>/Users/octocat</c> turned a
    /// sibling's <c>/Users/octocat2/…</c> into <c>~2/…</c> — and a new field is a new chance to
    /// reintroduce it.
    /// </summary>
    [Theory]
    // No path at all: byte-identical, or the parameter list stops being worth having.
    [InlineData("There was a problem adding channel: AI0.", "There was a problem adding channel: AI0.")]
    [InlineData("", "")]
    // A strict prefix of another account name. Both are scrubbed; neither is truncated.
    [InlineData("/Users/octocat2/logs/run.csv", "/Users/<account>/logs/run.csv")]
    // The account name mid-path, under a root that is not a home root: not an account segment, so
    // it stays. Anchoring on the /Users/ prefix is what makes this distinguishable at all.
    [InlineData("/opt/octocat/logs/run.csv", "/opt/octocat/logs/run.csv")]
    // An account name that is also an ordinary English word, in prose around a real path: the path
    // goes, the prose does not.
    [InlineData(
        "The sample at /Users/sam/samples/sample.csv was resampled",
        "The sample at /Users/<account>/samples/sample.csv was resampled")]
    public void A_log_message_parameter_is_redacted_no_further_than_the_account_segment(
        string parameter, string expected)
    {
        var @event = new SentryEvent
        {
            Message = new SentryMessage { Message = "{0}", Params = [parameter] }
        };

        var captured = Capture(@event, Shipped());

        Assert.Equal(expected, Assert.Single(captured.MessageParams).GetString());
    }

    #endregion

    #region Not over-redacting

    /// <summary>
    /// A message with no path in it must arrive byte-for-byte. The device friendly names, serial
    /// numbers and channel names that most <c>Error</c> messages carry are the reason an event is
    /// worth having, and a scrubber that mangled them would be a worse bug than the one it fixed.
    /// </summary>
    [Theory]
    [InlineData("There was a problem adding channel: AI0.")]
    [InlineData("Failed to connect to DAQiFi device at 192.168.1.1:9760")]
    [InlineData("Firmware upload failed in state Erasing: WriteFlash")]
    [InlineData("")]
    public void A_message_with_no_path_passes_through_unchanged(string message)
    {
        var captured = CaptureAsErrorWould(new InvalidOperationException("boom"), message);

        Assert.Equal(message, captured.Extra("message"));
    }

    /// <summary>
    /// The redaction is anchored on the <c>/Users/</c> segment, not on the account name as a bare
    /// word, so an account called <c>sam</c> does not turn "sample rate" into "&lt;account&gt;ple
    /// rate". Both halves are in ONE message here, because the failure mode is a scrubber that
    /// gets the path right and corrupts the prose around it.
    /// </summary>
    [Fact]
    public void An_account_name_that_is_a_substring_of_a_word_is_left_alone()
    {
        var captured = CaptureAsErrorWould(
            new InvalidOperationException("boom"),
            "The sample at /Users/sam/samples/sample.csv was resampled by Samantha");

        Assert.Equal(
            "The sample at /Users/<account>/samples/sample.csv was resampled by Samantha",
            captured.Extra("message"));
    }

    /// <summary>
    /// A profile directory more than one segment below the home root — what a domain-joined Linux
    /// box gives you — has to go WHOLE. The regex only ever eats one segment, so with the two
    /// substitutions in the other order it rewrote <c>/home/corp-eu</c> and left <c>octocat</c>,
    /// the actual account name, standing in every field the hooks now cover.
    ///
    /// <para>Against the helper rather than an envelope, because the profile directory is the
    /// input under test and no machine this suite runs on has a nested one — macOS and the CI
    /// runners are all flat. The wiring that carries this result into every field is what the
    /// envelope tests above establish.</para>
    /// </summary>
    [Theory]
    // A nested profile: BOTH segments must go, and the path tail must survive.
    [InlineData("/home/corp-eu/octocat", "/home/corp-eu/octocat/logs/run.csv", "~/logs/run.csv")]
    // The flat case, which the exact replacement also owns now that it runs first.
    [InlineData("/Users/octocat", "/Users/octocat/logs/run.csv", "~/logs/run.csv")]
    // Another account under the same nested root: the regex catches the root it can see. The
    // remaining segment is somebody else's name and not what #366 is about — pinned so a change
    // to it is deliberate rather than accidental.
    [InlineData("/home/corp-eu/octocat", "/home/corp-eu/hubot/logs/run.csv", "/home/<account>/hubot/logs/run.csv")]
    // No profile resolvable (GetFolderPath can fail): the regex still covers the usual shape.
    [InlineData("", "/Users/octocat/logs/run.csv", "/Users/<account>/logs/run.csv")]
    // Neither substitution applies.
    [InlineData("/Users/octocat", "There was a problem adding channel: AI0.", "There was a problem adding channel: AI0.")]
    public void A_profile_directory_is_redacted_whole_however_deep_it_sits(
        string profileDirectory, string message, string expected)
    {
        Assert.Equal(expected, AppLogger.RedactAccountNames(message, profileDirectory));
    }

    /// <summary>
    /// The profile directory has to match at a path boundary, not as a bare substring. An account
    /// called <c>octocat</c> shares a prefix with <c>octocat2</c>, and replacing blind turned
    /// <c>/Users/octocat2/logs/run.csv</c> into <c>~2/logs/run.csv</c> — another account's path
    /// corrupted rather than redacted, which is worse than either outcome on its own.
    ///
    /// <para>The sibling path is still scrubbed; it just falls through to the general rule and
    /// keeps its shape.</para>
    /// </summary>
    [Theory]
    // The sibling: NOT the profile, so it takes the general path and stays intact.
    [InlineData("/Users/octocat", "/Users/octocat2/logs/run.csv", "/Users/<account>/logs/run.csv")]
    // The profile itself, with nothing after it — end-of-string is a boundary too.
    [InlineData("/Users/octocat", "Wrote to /Users/octocat", "Wrote to ~")]
    // Windows separators, which the boundary has to accept as readily as '/'.
    [InlineData(@"C:\Users\octocat", @"C:\Users\octocat\Documents\run.csv", @"~\Documents\run.csv")]
    // A profile holding regex metacharacters: this is a pattern now, so it must be escaped.
    [InlineData("/Users/o+c(at", "/Users/o+c(at/logs/run.csv", "~/logs/run.csv")]
    public void A_sibling_of_the_profile_directory_is_not_truncated(
        string profileDirectory, string message, string expected)
    {
        Assert.Equal(expected, AppLogger.RedactAccountNames(message, profileDirectory));
    }

    #endregion

    #region Positive control

    /// <summary>
    /// The control, and the only test in this file that compiles against the merge base. It
    /// captures the #366 run-2 event through options with NO hooks — which is what the merge base
    /// configures, since none of the settings it does apply (release, environment, session
    /// tracking, <c>SendDefaultPii</c>, the cache directory) touch redaction — and asserts the
    /// account name reaches the envelope in both places.
    ///
    /// <para>Its job is to fail if this harness ever stops being able to SEE the leak. Every
    /// other assertion here is of the form "the account name is absent", and an assertion like
    /// that also passes when the harness is quietly broken and capturing nothing.</para>
    /// </summary>
    [Fact]
    public void Unhooked_options_leak_the_account_name()
    {
        var unhooked = new SentryOptions { Dsn = InertDsn, BackgroundWorker = new RecordingWorker() };

        var captured = CaptureAsErrorWould(
            new UnauthorizedAccessException($"Access to the path '{ProfilePath}' is denied."),
            $"Reading the profiles file failed: {ProfilePath}",
            unhooked);

        Assert.Contains(Account, captured.Extra("message"), StringComparison.Ordinal);
        Assert.Contains(Account, Assert.Single(captured.ExceptionValues), StringComparison.Ordinal);
    }

    #endregion

    #region Harness

    /// <summary>The configuration the app ships, straight off <see cref="AppLogger"/>.</summary>
    /// <remarks>
    /// The cache directory is cleared afterwards so the run writes no envelopes to disk; that is
    /// a delivery concern below the hooks, which <c>SentryClient</c> applies before an envelope
    /// exists at all.
    /// </remarks>
    private static SentryOptions Shipped()
    {
        var options = new SentryOptions();
        AppLogger.ConfigureSentryOptions(options, InertDsn, "1.2.3");
        options.CacheDirectoryPath = null;
        options.BackgroundWorker = new RecordingWorker();
        return options;
    }

    /// <summary>
    /// Captures the way <c>AppLogger.Error(Exception, string)</c> does — an event from the
    /// exception, with the contextual message on the scope as <c>extra["message"]</c>.
    /// </summary>
    private static CapturedEvent CaptureAsErrorWould(
        Exception exception, string message, SentryOptions? options = null)
    {
        options ??= Shipped();
        var scope = new Scope(options);
        scope.SetExtra("message", message);
        return Capture(new SentryEvent(exception), options, scope);
    }

    private static CapturedEvent Capture(SentryEvent @event, SentryOptions options, Scope? scope = null)
    {
        var worker = Assert.IsType<RecordingWorker>(options.BackgroundWorker);
        using var client = new SentryClient(options);

        client.CaptureEvent(@event, scope ?? new Scope(options));

        return new CapturedEvent(Assert.Single(worker.Envelopes));
    }

    /// <summary>
    /// Records envelopes instead of sending them. Replacing the worker rather than the transport
    /// is what keeps the SDK from constructing an HTTP transport at all: <c>SentryClient</c> only
    /// builds one when no worker was supplied.
    /// </summary>
    private sealed class RecordingWorker : IBackgroundWorker
    {
        private readonly List<string> _envelopes = [];

        public IReadOnlyList<string> Envelopes => _envelopes;

        public int QueuedItems => 0;

        public bool EnqueueEnvelope(Envelope envelope)
        {
            using var buffer = new MemoryStream();
            envelope.Serialize(buffer, null);
            _envelopes.Add(Encoding.UTF8.GetString(buffer.ToArray()));
            return true;
        }

        public Task FlushAsync(TimeSpan timeout) => Task.CompletedTask;
    }

    /// <summary>
    /// The event payload lifted back out of a serialised envelope, so assertions read the fields
    /// as Sentry would receive them rather than as objects this process still holds.
    /// </summary>
    private sealed class CapturedEvent
    {
        private readonly JsonElement _event;

        internal CapturedEvent(string envelope)
        {
            Raw = envelope;

            // Envelope framing: a header line, then alternating item-header / item-payload lines.
            var lines = envelope.Split('\n');
            var header = Array.FindIndex(lines, line => line.Contains("\"type\":\"event\"", StringComparison.Ordinal));
            Assert.InRange(header, 0, lines.Length - 2);

            // Cloned, so the element outlives the document's pooled buffers.
            using var document = JsonDocument.Parse(lines[header + 1]);
            _event = document.RootElement.Clone();
        }

        /// <summary>The whole envelope as text — for "this string appears nowhere" assertions.</summary>
        internal string Raw { get; }

        internal string? Extra(string key) =>
            _event.GetProperty("extra").GetProperty(key).GetString();

        internal IEnumerable<string?> ExceptionValues =>
            Values("exception").Select(exception => exception.GetProperty("value").GetString());

        internal IEnumerable<string?> StackFrameFileNames =>
            Values("exception")
                .SelectMany(exception => exception.GetProperty("stacktrace").GetProperty("frames").EnumerateArray())
                .Select(frame => frame.GetProperty("filename").GetString());

        internal IEnumerable<string?> BreadcrumbMessages =>
            Values("breadcrumbs").Select(breadcrumb => breadcrumb.GetProperty("message").GetString());

        internal IEnumerable<string?> BreadcrumbData(string key) =>
            Values("breadcrumbs")
                .Select(breadcrumb => breadcrumb.GetProperty("data").GetProperty(key).GetString());

        /// <summary>A field of the <c>logentry</c> interface — <c>message</c> or <c>formatted</c>.</summary>
        internal string? LogEntry(string field) =>
            _event.GetProperty("logentry").GetProperty(field).GetString();

        /// <summary>
        /// <c>logentry.params</c> as elements rather than strings, so a test can assert what a
        /// parameter's JSON kind is and not only what it reads as.
        /// </summary>
        internal IReadOnlyList<JsonElement> MessageParams =>
            [.. _event.GetProperty("logentry").GetProperty("params").EnumerateArray()];

        internal string? ServerName => _event.GetProperty("server_name").GetString();

        /// <summary>Whether the event carries a field at all, for the fields that should be absent.</summary>
        internal bool Has(string name) => _event.TryGetProperty(name, out _);

        /// <summary>
        /// Sentry writes some interfaces as a bare array and others wrapped in <c>{"values":[…]}</c>;
        /// which one is a serialisation detail this file has no business pinning.
        /// </summary>
        private IEnumerable<JsonElement> Values(string name)
        {
            if (!_event.TryGetProperty(name, out var node)) { return []; }

            return node.ValueKind == JsonValueKind.Object
                ? node.GetProperty("values").EnumerateArray()
                : node.EnumerateArray();
        }
    }

    #endregion
}
