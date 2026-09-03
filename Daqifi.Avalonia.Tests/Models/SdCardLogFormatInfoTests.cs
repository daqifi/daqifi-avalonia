using Avalonia.Platform.Storage;
using Daqifi.Core.Device.SdCard;
using Daqifi.Desktop.Models;
using Xunit;

namespace Daqifi.Avalonia.Tests.Models;

/// <summary>
/// Pins that the SD log format labels and the import picker's filters both come from Core's
/// <see cref="SdCardFileParserFactory"/> rather than a list copied into this repo.
/// </summary>
/// <remarks>
/// These assertions are deliberately written against <c>SupportedExtensions</c> instead of a
/// literal <c>{ ".bin", ".json", ".csv" }</c>. A test that hardcodes the list would re-introduce
/// exactly the duplication the type exists to remove — it would keep passing after Core adds a
/// format the app then fails to offer, which is the bug (#222). Written this way, a Core release
/// that changes the set is caught here.
/// <para>
/// <see cref="FilePickerFileType"/> is a plain data type from <c>Avalonia.Platform.Storage</c>;
/// constructing one starts no application lifetime and needs no windowing platform, so this stays
/// within the project's library-code-only rule.
/// </para>
/// </remarks>
public class SdCardLogFormatInfoTests
{
    [Fact]
    public void EveryExtensionCoreSupports_HasANonUnknownLabel()
    {
        Assert.NotEmpty(SdCardFileParserFactory.SupportedExtensions);

        foreach (var extension in SdCardFileParserFactory.SupportedExtensions)
        {
            var display = SdCardLogFormatInfo.DisplayNameFor($"log_20260623_143217{extension}");

            Assert.NotEqual(SdCardLogFormatInfo.UNKNOWN_FORMAT_DISPLAY, display);
            Assert.False(string.IsNullOrWhiteSpace(display));
        }
    }

    [Theory]
    [InlineData("log.bin", "Protobuf")]
    [InlineData("log.json", "JSON")]
    [InlineData("log.csv", "CSV")]
    public void KnownFormats_GetTheirUserFacingLabel(string fileName, string expected)
    {
        Assert.Equal(expected, SdCardLogFormatInfo.DisplayNameFor(fileName));
    }

    [Fact]
    public void ExtensionMatching_IsCaseInsensitive()
    {
        Assert.Equal(
            SdCardLogFormatInfo.DisplayNameFor("log.bin"),
            SdCardLogFormatInfo.DisplayNameFor("LOG.BIN"));
    }

    [Theory]
    [InlineData("readme.txt")]   // an extension Core does not parse
    [InlineData("LOG")]          // no extension at all
    [InlineData("")]
    [InlineData("   ")]
    public void UnrecognizedNames_ReadAsUnknown(string fileName)
    {
        Assert.Equal(SdCardLogFormatInfo.UNKNOWN_FORMAT_DISPLAY,
            SdCardLogFormatInfo.DisplayNameFor(fileName));
    }

    [Fact]
    public void PickerFilters_CoverEveryExtensionCoreSupports()
    {
        var types = SdCardLogFormatInfo.BuildFilePickerFileTypes();
        var everyPattern = types.SelectMany(t => t.Patterns ?? Array.Empty<string>()).ToList();

        foreach (var extension in SdCardFileParserFactory.SupportedExtensions)
        {
            Assert.Contains($"*{extension}", everyPattern);
        }
    }

    [Fact]
    public void PickerFilters_LeadWithACombinedEntryThenOnePerFormatThenAllFiles()
    {
        var types = SdCardLogFormatInfo.BuildFilePickerFileTypes();
        var formatCount = SdCardFileParserFactory.SupportedExtensions.Count;

        // combined + one per format + "All Files"
        Assert.Equal(formatCount + 2, types.Count);

        // The combined entry offers every extension at once, so a user who does not care about
        // the format still sees all of their logs.
        Assert.Equal(formatCount, types[0].Patterns?.Count);

        // Each per-format entry is a single extension, labeled with the same name the session
        // list shows, so the two cannot disagree about what a ".bin" is called.
        foreach (var (type, extension) in types
                     .Skip(1).Take(formatCount)
                     .Zip(SdCardFileParserFactory.SupportedExtensions))
        {
            Assert.Equal(new[] { $"*{extension}" }, type.Patterns);
            Assert.StartsWith(SdCardLogFormatInfo.DisplayNameFor($"*{extension}"), type.Name);
        }

        Assert.Same(FilePickerFileTypes.All, types[^1]);
    }

    [Fact]
    public void SdCardFile_LabelsItselfThroughTheSameAuthority()
    {
        var file = new SdCardFile { FileName = "log_20260623_143217.json" };

        Assert.Equal(SdCardLogFormatInfo.DisplayNameFor(file.FileName), file.FormatDisplay);
        Assert.Equal("JSON", file.FormatDisplay);
    }
}
