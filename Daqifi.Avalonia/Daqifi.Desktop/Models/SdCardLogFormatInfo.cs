using Avalonia.Platform.Storage;
using Daqifi.Core.Device.SdCard;

namespace Daqifi.Desktop.Models;

/// <summary>
/// User-facing presentation for the SD card log formats Core can parse.
/// </summary>
/// <remarks>
/// Core's <see cref="SdCardFileParserFactory"/> is the single authority on which extensions are
/// recognized and which format each maps to; this type only supplies the display text and the
/// file-picker filters built from that list. Keeping the extension list out of the app means a
/// format Core gains is offered for import automatically, and one it drops stops being offered —
/// neither needs an edit here.
/// <para>
/// Upstream's equivalent returns a WPF <c>OpenFileDialog.Filter</c> string; this returns Avalonia
/// <see cref="FilePickerFileType"/> values, because that is what <c>IStorageProvider</c> takes.
/// The capability is the same and the authority is the same — only the shape the platform wants
/// differs.
/// </para>
/// </remarks>
// @port: Daqifi.Desktop.Models.SdCardLogFormatInfo
public static class SdCardLogFormatInfo
{
    #region Constants
    /// <summary>Label used when Core cannot parse the file's extension.</summary>
    // @port: Daqifi.Desktop.Models.SdCardLogFormatInfo.UNKNOWN_FORMAT_DISPLAY
    public const string UNKNOWN_FORMAT_DISPLAY = "Unknown";
    #endregion

    #region Public Methods
    /// <summary>
    /// Maps a file name to a user-facing format label, or
    /// <see cref="UNKNOWN_FORMAT_DISPLAY"/> when Core does not recognize the extension.
    /// </summary>
    /// <param name="fileName">The file name or path to label.</param>
    /// <returns>
    /// The user-facing label for the detected format, the format's enum name when Core recognizes
    /// a format this app has no label for, or <see cref="UNKNOWN_FORMAT_DISPLAY"/> when Core does
    /// not recognize the extension.
    /// </returns>
    // @port: Daqifi.Desktop.Models.SdCardLogFormatInfo.DisplayNameFor
    public static string DisplayNameFor(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            !SdCardFileParserFactory.TryDetectFormat(fileName, out var format))
        {
            return UNKNOWN_FORMAT_DISPLAY;
        }

        return format switch
        {
            SdCardLogFormat.Protobuf => "Protobuf",
            SdCardLogFormat.Json => "JSON",
            SdCardLogFormat.Csv => "CSV",
            // A format Core recognizes but this app has no label for yet — show the enum name
            // rather than claiming the file is unimportable.
            _ => format.ToString()
        };
    }

    /// <summary>
    /// Builds the import picker's <c>FileTypeFilter</c> covering every format Core can parse: one
    /// combined entry, one entry per format, then "All Files".
    /// </summary>
    /// <returns>
    /// The filter list, ordered as the combined group, one entry per format Core supports, then
    /// <see cref="FilePickerFileTypes.All"/>.
    /// </returns>
    // @port: Daqifi.Desktop.Models.SdCardLogFormatInfo.BuildOpenFileDialogFilter
    public static IReadOnlyList<FilePickerFileType> BuildFilePickerFileTypes()
    {
        // "*.bin" rather than a bare ".bin" so the label lookup's Path.GetExtension sees a file
        // name that has an extension, not a name that merely starts with a dot.
        var patterns = SdCardFileParserFactory.SupportedExtensions
            .Select(extension => $"*{extension}")
            .ToList();

        var types = new List<FilePickerFileType>
        {
            new($"SD Card Log Files ({string.Join(";", patterns)})") { Patterns = patterns }
        };

        types.AddRange(patterns.Select(pattern =>
            new FilePickerFileType($"{DisplayNameFor(pattern)} ({pattern})")
            {
                Patterns = new[] { pattern }
            }));

        types.Add(FilePickerFileTypes.All);
        return types;
    }
    #endregion
}
