// Downstream addition — no upstream counterpart, so no `// @port:` marker.

using Microsoft.EntityFrameworkCore;

namespace Daqifi.Desktop.Logger;

/// <summary>
/// Hands out the next <see cref="LoggingSession.ID"/>. One implementation, because the two callers
/// that need one had drifted: <c>LoggingManager</c> took the maximum across all three related
/// tables and explained at length why, while <c>SdCardSessionImporter</c> took
/// <c>MAX(Sessions.ID) + 1</c> — the exact narrow version that comment was warning about.
/// </summary>
internal static class SessionIdAllocator
{
    /// <summary>
    /// Picks an ID that cannot collide with any existing row in any related table.
    /// </summary>
    /// <param name="context">An open logging context; the caller owns its lifetime.</param>
    /// <returns>An ID no row in <c>Sessions</c>, <c>SessionDeviceMetadata</c> or <c>Samples</c> uses.</returns>
    /// <remarks>
    /// <para><c>Sessions.ID</c> is manually assigned (not IDENTITY), so reusing the max+1 across
    /// only the <c>Sessions</c> table can hand out an ID that is still referenced by orphan rows in
    /// <c>SessionDeviceMetadata</c> or <c>Samples</c> — from a prior crash, or a delete that ran
    /// without SQLite foreign keys enabled. The composite PK on <c>SessionDeviceMetadata</c> then
    /// rejects the insert with UNIQUE constraint failed and the logging toggle appears to do
    /// nothing on the second attempt.</para>
    /// <para>One of the callers runs synchronously on the UI thread (<c>IsLogging</c> is a UI-bound
    /// toggle), so the cost matters as the database grows. Folding the three MAXes into a single
    /// SQL statement keeps the round-trip count at one. Each inner MAX hits an index —
    /// <c>Sessions.ID</c> is the PK, <c>SessionDeviceMetadata.LoggingSessionID</c> is the leading
    /// column of the composite PK, and <c>Samples</c> has <c>IX_Samples_SessionTime</c> on
    /// <c>(LoggingSessionID, TimestampTicks)</c> — so SQLite resolves each MAX as an index seek
    /// rather than a table scan.</para>
    /// </remarks>
    internal static int NextSessionId(LoggingContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        const string nextSessionIdSql = @"
            SELECT MAX(id) AS Value FROM (
                SELECT MAX(ID) AS id FROM Sessions
                UNION ALL SELECT MAX(LoggingSessionID) AS id FROM SessionDeviceMetadata
                UNION ALL SELECT MAX(LoggingSessionID) AS id FROM Samples
            )";

        var ctx = context;
        var maxKnownId = ctx.Database.SqlQueryRaw<int?>(nextSessionIdSql)
            .AsEnumerable()
            .FirstOrDefault() ?? -1;

        return maxKnownId + 1;
    }
}
