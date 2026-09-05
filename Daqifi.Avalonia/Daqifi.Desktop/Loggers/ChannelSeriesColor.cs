using OxyPlot;

namespace Daqifi.Desktop.Logger;

/// <summary>
/// The one place that turns a STORED channel colour string into the <see cref="OxyColor"/> a series
/// is drawn in. Every <see cref="OxyColor.Parse"/> call on this path goes through here, so what an
/// unusable colour becomes is decided once instead of at each caller (issue #231).
///
/// <para>
/// It exists because the stored value is not trustworthy. <c>Samples.Color</c> is declared
/// <c>NOT NULL</c>, but that binds the table as the migration creates it — SQLite does not
/// re-validate rows already in an existing database file, and upstream daqifi-desktop's own comment
/// above its <c>FALLBACK_CHANNEL_COLOR</c> names the case: legacy and imported rows can omit the
/// value. <c>NOT NULL</c> has never excluded <c>""</c> either.
/// </para>
///
/// <para>
/// Handing such a value to <see cref="OxyColor.Parse"/> directly fails in two different ways, both
/// measured against OxyPlot.Core 2.2.0 and both bad:
/// </para>
/// <list type="bullet">
/// <item>a blank or unparseable string throws <see cref="FormatException"/>, which escapes channel
/// discovery and aborts the WHOLE session load — one colourless row, and the user's session will not
/// open at all;</item>
/// <item>a null does not throw. It yields <c>OxyColors.Undefined</c>, whose alpha is zero, and
/// OxyPlot substitutes a palette colour only for <c>Automatic</c> — so the series is drawn fully
/// transparent and the channel silently is not on the plot.</item>
/// </list>
/// </summary>
internal static class ChannelSeriesColor
{
    /// <summary>
    /// Colour used when a stored colour cannot be drawn. Matches upstream daqifi-desktop's
    /// <c>SessionDataRepository.FALLBACK_CHANNEL_COLOR</c> exactly, so the two apps render the same
    /// legacy session the same way, and it is the same grey the mobile viewer's legend already falls
    /// back to (Avalonia's <c>Colors.Gray</c>) — the chip and the line agree without either knowing
    /// about the other.
    /// </summary>
    internal const string FALLBACK_CHANNEL_COLOR = "#FF808080";

    private static readonly OxyColor Fallback = OxyColor.Parse(FALLBACK_CHANNEL_COLOR);

    /// <summary>
    /// The colour <paramref name="storedColor"/> asks for, or <see cref="FALLBACK_CHANNEL_COLOR"/>
    /// when it asks for nothing usable. Never throws: a colour that cannot be read is a reason to
    /// draw the channel grey, never a reason to lose the channel or the session.
    /// </summary>
    /// <param name="storedColor">The colour string as persisted with the samples, e.g. "#FFD32F2F".</param>
    internal static OxyColor ParseOrFallback(string? storedColor)
    {
        // Null and blank are checked first rather than left to the catch, because null does not throw
        // — it parses to a transparent colour, which is the silent failure this exists to prevent.
        if (string.IsNullOrWhiteSpace(storedColor))
        {
            return Fallback;
        }

        try
        {
            return OxyColor.Parse(storedColor);
        }
        catch (Exception)
        {
            // Deliberately unfiltered. OxyColor.Parse documents no exception contract and reaches
            // uint.Parse and a reflected OxyColors lookup by different routes, so the set it can throw
            // is not ours to enumerate — and getting that list wrong would put the crash back. The
            // single guarded statement is a parse of a value we already distrust, so there is no
            // failure of our own for this to swallow.
            return Fallback;
        }
    }
}
