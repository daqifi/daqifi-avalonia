// DO NOT manually delete the `// @port:` markers — they link symbols back to
// the correspondence map.

using System.IO;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Daqifi.Desktop.Common.Loggers;
using Daqifi.Desktop.Exporter;
using Daqifi.Desktop.Services;
using OxyPlot;
// Alias (not `using OxyPlot.Avalonia;`) — that namespace re-declares LineSeries etc.
using PngExporter = OxyPlot.Avalonia.PngExporter;
// Sentry declares a BreadcrumbLevel of its own, and the Sentry namespace is in scope here.
using BreadcrumbLevel = Daqifi.Desktop.Common.Loggers.BreadcrumbLevel;

namespace Daqifi.Desktop.Logger;

/// <summary>
/// The "save this graph as a PNG" gesture: ask the user where, render, write, and — if the write
/// does not work — say so. The only implementation of it, shared by <b>Save Graph</b> on the Logged
/// Data pane (<see cref="DatabaseLogger"/>) and <b>Save Live Graph</b> on the Live Graph pane
/// (<see cref="PlotLogger"/>).
/// </summary>
/// <remarks>
/// <para>
/// Both commands previously carried their own byte-identical copy of the picker-and-export block,
/// with no <c>try</c> anywhere in it. Because they are <c>[RelayCommand]</c> methods built with the
/// default <c>AsyncRelayCommandOptions.None</c>, an exception out of the write was rethrown on the
/// UI <c>SynchronizationContext</c> by <c>AwaitAndThrowIfFailed</c>, reached
/// <c>Dispatcher.UIThread.UnhandledException</c>, and — since
/// <c>App.OnDispatcherUnhandledException</c> logs without setting <c>e.Handled</c> — terminated the
/// process. Picking a read-only folder, or a share that had been unmounted since the picker opened,
/// made the app vanish with no message (issue #182).
/// </para>
/// <para>
/// So the guarantee here is not merely "does not throw": the user asked to save a graph and must
/// learn that it did not save, and why. Failures are classified by the same
/// <see cref="DestinationFailureClassifier"/> the CSV export uses, so an unwritable destination
/// gets identical wording and identical Warning-not-Error log treatment on both paths.
/// </para>
/// </remarks>
internal static class GraphImageSaver
{
    #region Constants
    /// <summary>Exported image size, unchanged from the two copies this replaces.</summary>
    private const int WIDTH_PIXELS = 1024;
    private const int HEIGHT_PIXELS = 768;

    /// <summary>
    /// What to call the file in a failure message when the path is somehow blank. The picker always
    /// supplies one, so this is a backstop rather than a routine case.
    /// </summary>
    private const string GRAPH_IMAGE = "the graph image";

    /// <summary>Shown when the destination is fine but the plot itself could not be rendered.</summary>
    private const string RENDER_FAILED =
        "Could not save the graph — the image could not be created. Please try again.";
    #endregion

    #region Private Variables
    /// <summary>
    /// Matches <see cref="Daqifi.Desktop.ViewModels.DeviceLogsViewModel"/>'s dialect: a shared
    /// service instance rather than an injected one, since the whole method is inert without a
    /// desktop lifetime anyway.
    /// </summary>
    private static readonly IMessageBoxService MessageBox = new AvaloniaMessageBoxService();
    #endregion

    #region Public Methods
    /// <summary>
    /// Runs the whole gesture. Returns normally whatever happens — including when the user cancels
    /// the picker, which is silent.
    /// </summary>
    /// <param name="plotModel">The plot to export.</param>
    /// <param name="caption">
    /// Title for the failure dialog, so it names the button the user actually pressed
    /// ("Save Graph" / "Save Live Graph").
    /// </param>
    internal static async Task SaveAsync(PlotModel plotModel, string caption)
    {
        // Ownerless Win32 SaveFileDialog → StorageProvider picker owned by the app main
        // window (hal: file_pickers), matching the ownerless-dialog dialect in
        // AvaloniaMessageBoxService. OxyPlot.Wpf.PngExporter → OxyPlot.Avalonia.PngExporter.
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } main })
        {
            return; // headless run — nothing can own a picker
        }

        string? path;
        try
        {
            var file = await main.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                DefaultExtension = "png",
                FileTypeChoices = new[] { FilePickerFileTypes.ImagePng }
            });

            path = file?.TryGetLocalPath();
        }
        catch (Exception ex)
        {
            // Defensive, and beyond what issue #182 reported: the picker is a platform component
            // (an unavailable xdg-desktop-portal on Linux is the realistic case) and a throw here
            // would kill the app exactly as the unguarded write did. Not reproduced.
            AppLogger.Instance.Error(ex, "Problem showing the save-graph file picker.");
            await ReportAsync("Could not open the save dialog. Please try again.", caption);
            return;
        }

        if (path == null) { return; } // cancelled, or a location with no local path

        var failure = SaveTo(path, stream => Render(plotModel, stream));
        if (failure != null)
        {
            await ReportAsync(failure, caption);
        }
    }

    /// <summary>
    /// Renders through <paramref name="render"/> and writes the result to <paramref name="path"/>.
    /// Returns <c>null</c> when the file was written, otherwise the sentence to show the user.
    /// Never throws — this is what stands between an unwritable destination and a dead process.
    /// </summary>
    /// <param name="path">Destination chosen by the user.</param>
    /// <param name="render">
    /// Writes the image into the supplied stream. A delegate rather than a
    /// <see cref="PlotModel"/> so the failure handling can be tested without an Avalonia render
    /// backend — <c>PngExporter</c> goes through <c>RenderTargetBitmap</c>, which needs a platform
    /// rendering interface that a plain xUnit host does not have.
    /// </param>
    internal static string? SaveTo(string path, Action<Stream> render)
    {
        // Render into memory BEFORE opening the destination. File.Create truncates, so rendering
        // straight into it — as both copies of this code did — means a rendering failure leaves an
        // empty file where the user's previous graph was. This way the destination is only touched
        // once there are bytes to put in it, and it is held open for the length of a
        // memory-to-disk copy rather than a full plot render.
        using var image = new MemoryStream();
        try
        {
            render(image);
            image.Position = 0;
        }
        catch (Exception ex)
        {
            // Kept separate from the write failures below so the classifier cannot mistake a
            // rendering fault for a destination fault and tell the user to pick a different folder.
            AppLogger.Instance.Error(ex, "Problem rendering the graph image.");
            AppLogger.Instance.AddBreadcrumb("graph", "Graph image render failed", BreadcrumbLevel.Error);
            return RENDER_FAILED;
        }

        try
        {
            WriteFile(path, image);
            return null;
        }
        catch (Exception ex) when (DestinationFailureClassifier.IsBlocked(ex))
        {
            // A destination that cannot be written — read-only, denied, held by another program, or
            // on a folder that disappeared — is a user/environmental condition, not an app bug, so
            // it logs at Warning and raises no Sentry issue. Mirrors the CSV export path.
            AppLogger.Instance.Warning(ex, $"Saving the graph image to '{path}' was blocked by the destination.");
            AppLogger.Instance.AddBreadcrumb("graph", "Graph image save blocked by destination",
                BreadcrumbLevel.Warning);
            return DestinationFailureClassifier.Describe(ex, path, GRAPH_IMAGE);
        }
        catch (Exception ex)
        {
            // Anything else — a full disk, a failing drive — keeps the Error/Sentry path, but still
            // reaches the user as a message rather than as a missing application.
            AppLogger.Instance.Error(ex, $"Problem saving the graph image to '{path}'.");
            AppLogger.Instance.AddBreadcrumb("graph", "Graph image save failed", BreadcrumbLevel.Error);
            return DestinationFailureClassifier.Describe(ex, path, GRAPH_IMAGE);
        }
    }

    /// <summary>
    /// Copies the rendered bytes onto disk, cleaning up after a write that starts and then fails.
    /// Throws on failure; <see cref="SaveTo"/> is what turns that into a message.
    /// </summary>
    /// <remarks>
    /// Internal rather than private so a test can drive the mid-write failure — a destination that
    /// runs out of room after the file is created — which cannot be provoked through
    /// <see cref="SaveTo"/>, whose source stream is a <see cref="MemoryStream"/> it owns.
    /// </remarks>
    internal static void WriteFile(string path, Stream image)
    {
        // If File.Create itself throws, nothing was created or truncated and there is nothing to
        // clean up — so it stays outside the try below, which exists only to undo a partial write.
        var destination = File.Create(path);
        try
        {
            using (destination)
            {
                image.CopyTo(destination);
            }
        }
        catch
        {
            // The copy, or the flush on dispose, failed part-way — a full disk, a volume that went
            // away mid-write. What is on disk now is a truncated husk, not a PNG; leaving it would
            // hand the user a file that opens broken some time after they were told the save
            // failed. (An existing file at this path was already lost to File.Create's truncation;
            // the picker having confirmed the overwrite is what makes that acceptable.)
            TryDeletePartialFile(path);
            throw;
        }
    }
    #endregion

    #region Private Methods
    /// <summary>Renders <paramref name="plotModel"/> as a PNG into <paramref name="stream"/>.</summary>
    private static void Render(PlotModel plotModel, Stream stream)
    {
        var pngExporter = new PngExporter { Width = WIDTH_PIXELS, Height = HEIGHT_PIXELS };
        pngExporter.Export(plotModel, stream);
    }

    /// <summary>Best-effort removal of a half-written image; a failure here must not mask the
    /// original write failure, which is the one the user needs to hear about.</summary>
    private static void TryDeletePartialFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            AppLogger.Instance.Warning(ex, $"Could not remove the partly-written graph image '{path}'.");
        }
    }

    private static Task ReportAsync(string message, string caption) =>
        MessageBox.ShowAsync(message, caption, MessageBoxButton.OK, MessageBoxImage.Error);
    #endregion
}
