using System.Runtime.InteropServices;
using Daqifi.Desktop.Logger;
using Xunit;

namespace Daqifi.Avalonia.Tests.Loggers;

/// <summary>
/// Pins that saving a graph to a destination that cannot be written reports the failure instead of
/// killing the application.
///
/// <para>
/// The regression (issue #182): <c>DatabaseLogger.SaveGraphAsync</c> and
/// <c>PlotLogger.SaveLiveGraphAsync</c> each carried their own byte-identical copy of
/// <c>using var stream = File.Create(path); pngExporter.Export(PlotModel, stream);</c> with no
/// <c>try</c> anywhere. Both are <c>[RelayCommand]</c> methods on default
/// <c>AsyncRelayCommandOptions</c>, so the exception was rethrown on the UI
/// <c>SynchronizationContext</c>, reached <c>Dispatcher.UIThread.UnhandledException</c>, and — since
/// <c>App.OnDispatcherUnhandledException</c> logs without setting <c>e.Handled</c> — terminated the
/// process. The user picked a folder they could not write to and the app simply vanished.
/// </para>
///
/// <para>
/// Every test below therefore does two things: it first asserts that the RAW operation the old code
/// performed still throws — that is the crash, reproduced, and it keeps these vectors honest if a
/// future .NET changes which exception a bad destination raises — and then asserts that
/// <see cref="GraphImageSaver"/> turns the same vector into a sentence for the user.
/// </para>
/// </summary>
public sealed class GraphImageSaveTests : IDisposable
{
    /// <summary>Throwaway root for this test's destinations. Never the real DAQiFi data directory
    /// (see <see cref="TestDataDirectory"/>).</summary>
    private readonly string _root;

    /// <summary>Stands in for a rendered PNG. The bytes are the real PNG magic number so a
    /// success assertion is about identifiable content rather than "the file is non-empty".</summary>
    private static readonly byte[] ImageBytes = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A];

    public GraphImageSaveTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "daqifi-graph-save-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        // A test may have left a directory read-only; make it removable again before cleanup, or
        // the throwaway root survives the run.
        if (!OperatingSystem.IsWindows())
        {
            foreach (var directory in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories))
            {
                try { File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
                catch (IOException) { /* best effort */ }
                catch (UnauthorizedAccessException) { /* best effort */ }
            }
        }

        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* a leftover temp directory must not fail a test */ }
        catch (UnauthorizedAccessException) { /* ditto */ }
    }

    /// <summary>
    /// The issue's headline vector: a folder the user cannot write to. Chosen by a picker that
    /// happily lets them pick it, because the picker asks the platform for a path, not for
    /// permission to use it.
    /// </summary>
    /// <remarks>
    /// Skipped where the environment does not actually enforce the mode — Windows, which has no
    /// <c>UnixFileMode</c>, and any run as root, where 0555 is advisory. Rather than guessing at
    /// the identity, the helper verifies enforcement by trying a write. The exception type this
    /// vector produces is pinned unconditionally by
    /// <see cref="Destination_that_is_a_directory_is_reported_not_thrown"/>, which needs no
    /// permissions at all, so CI still covers the wording when this one stands down.
    /// </remarks>
    [Fact]
    public void Read_only_destination_folder_is_reported_not_thrown()
    {
        var directory = CreateEnforcedReadOnlyDirectory();
        if (directory == null) { return; }

        var path = Path.Combine(directory, "graph.png");

        // The crash: this is verbatim what both commands used to do, unguarded.
        Assert.Throws<UnauthorizedAccessException>(() => File.Create(path));

        var failure = GraphImageSaver.SaveTo(path, WriteImage);

        Assert.NotNull(failure);
        Assert.Contains("access was denied", failure, StringComparison.Ordinal);
        Assert.Contains("graph.png", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// The same <see cref="UnauthorizedAccessException"/> as the read-only folder, reached without
    /// any permission bits: <see cref="File.Create(string)"/> refuses a path that names an existing
    /// directory, on every platform and as any user. This is the test that keeps the denied-access
    /// wording covered on CI, where the run may well be root.
    /// </summary>
    [Fact]
    public void Destination_that_is_a_directory_is_reported_not_thrown()
    {
        var path = Path.Combine(_root, "graph.png");
        Directory.CreateDirectory(path);

        Assert.Throws<UnauthorizedAccessException>(() => File.Create(path));

        var failure = GraphImageSaver.SaveTo(path, WriteImage);

        Assert.NotNull(failure);
        Assert.Contains("access was denied", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// The issue's "unmounted between picking the path and the write" vector: the folder the user
    /// chose is not there any more by the time the bytes are written.
    /// </summary>
    [Fact]
    public void Destination_folder_that_disappeared_is_reported_not_thrown()
    {
        var path = Path.Combine(_root, "gone", "graph.png");

        Assert.Throws<DirectoryNotFoundException>(() => File.Create(path));

        var failure = GraphImageSaver.SaveTo(path, WriteImage);

        Assert.NotNull(failure);
        Assert.Contains("no longer exists", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// A path whose parent is a regular file. Unlike the vectors above this one's exception type is
    /// not the same everywhere, so the assertion is the guarantee that actually matters — the call
    /// returns a message rather than throwing — instead of a specific sentence.
    /// </summary>
    [Fact]
    public void Destination_whose_parent_is_a_file_is_reported_not_thrown()
    {
        var parent = Path.Combine(_root, "not-a-folder");
        File.WriteAllText(parent, "this is a file");
        var path = Path.Combine(parent, "graph.png");

        Assert.ThrowsAny<IOException>(() => File.Create(path));

        var failure = GraphImageSaver.SaveTo(path, WriteImage);

        Assert.NotNull(failure);
        Assert.Contains("Could not write", failure, StringComparison.Ordinal);
    }

    /// <summary>
    /// The ordinary case, so the fix cannot be "always fail": a writable destination reports no
    /// failure and receives exactly the rendered bytes.
    /// </summary>
    [Fact]
    public void Writable_destination_receives_the_rendered_image()
    {
        var path = Path.Combine(_root, "graph.png");

        var failure = GraphImageSaver.SaveTo(path, WriteImage);

        Assert.Null(failure);
        Assert.Equal(ImageBytes, File.ReadAllBytes(path));
    }

    /// <summary>
    /// Overwriting an existing graph still works — the picker's own "replace?" prompt is what
    /// authorises it, and this pins that the guarded path did not turn overwrite into a failure.
    /// </summary>
    [Fact]
    public void Existing_file_is_replaced_by_the_new_image()
    {
        var path = Path.Combine(_root, "graph.png");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09]);

        var failure = GraphImageSaver.SaveTo(path, WriteImage);

        Assert.Null(failure);
        // Equality, not "starts with": File.Create truncates, and a shorter new image must not
        // leave the tail of the old one behind.
        Assert.Equal(ImageBytes, File.ReadAllBytes(path));
    }

    /// <summary>
    /// A rendering failure is reported too — it was the other half of the unguarded block — and,
    /// because the plot is rendered into memory before the destination is opened, it leaves the
    /// destination untouched. The old code opened the file first, so a render that threw left an
    /// empty file where the user's previous graph had been.
    /// </summary>
    [Fact]
    public void Failed_render_is_reported_and_leaves_the_destination_untouched()
    {
        var path = Path.Combine(_root, "graph.png");
        File.WriteAllBytes(path, ImageBytes);

        var failure = GraphImageSaver.SaveTo(path, _ => throw new InvalidOperationException("render blew up"));

        Assert.NotNull(failure);
        Assert.Contains("image could not be created", failure, StringComparison.Ordinal);
        // The previously saved graph is still exactly as it was.
        Assert.Equal(ImageBytes, File.ReadAllBytes(path));
    }

    /// <summary>
    /// A write that starts and then fails part-way — the issue's "fill the disk while the PNG is
    /// being written" case — must not leave a truncated husk behind. Half a PNG is a file the user
    /// opens later and finds broken, some time after being told the save failed.
    /// </summary>
    [Fact]
    public void Write_that_fails_part_way_leaves_no_husk()
    {
        var path = Path.Combine(_root, "graph.png");

        Assert.Throws<IOException>(() => GraphImageSaver.WriteFile(path, new FailingStream()));

        Assert.False(File.Exists(path), "a partly-written image was left at the destination");
    }

    /// <summary>Fills the stream with <see cref="ImageBytes"/>, standing in for the PNG exporter.</summary>
    private static void WriteImage(Stream stream) => stream.Write(ImageBytes);

    /// <summary>
    /// A directory the current user genuinely cannot write to, or null when this environment does
    /// not enforce that — Windows (no <see cref="UnixFileMode"/>) or a run as root. Enforcement is
    /// confirmed by attempting a write rather than inferred from the user's identity.
    /// </summary>
    private string? CreateEnforcedReadOnlyDirectory()
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && !RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            return null;
        }

        var directory = Path.Combine(_root, "read-only");
        Directory.CreateDirectory(directory);
        File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        try
        {
            // If this succeeds the mode is advisory here (root), and a test built on it would pass
            // for the wrong reason.
            File.Create(Path.Combine(directory, "enforcement-probe")).Dispose();
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return directory;
        }
    }

    /// <summary>
    /// A readable stream that faults once copying starts, standing in for a destination that runs
    /// out of room mid-write.
    /// </summary>
    private sealed class FailingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => 0;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("There is not enough space on the disk.");
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
