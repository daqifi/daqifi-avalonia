// Port of upstream daqifi-desktop#691 ("chore: drop vestigial Channel EF entity and its table").
// Upstream's migration is 20260714120000_DropChannelTable; this one carries over its IF EXISTS
// guards and leaves out its unrelated Samples index rename (see the remarks below).
//
// @port: Daqifi.Desktop.Migrations.DropChannelTable

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daqifi.Desktop.Migrations;

/// <summary>
/// Drops the <c>Channel</c> table, which <see cref="InitialSQLiteMigration"/> created and nothing
/// ever wrote to or read from. Its entity type (<c>Daqifi.Desktop.Channel.Channel</c>) was never
/// constructed, had no <c>DbSet</c>, and was reachable only through the never-populated
/// <c>LoggingSession.Channels</c> navigation property; all three are deleted alongside this
/// migration. See daqifi/daqifi-avalonia#206.
/// </summary>
/// <remarks>
/// <para><b>What this does to a real database.</b> The table is always empty in any database this
/// application produced, so dropping it destroys no logged data — sessions, samples and device
/// metadata are untouched. It is still a schema change against files users already have, so the
/// drop is written to survive every shape one can be in:</para>
/// <list type="bullet">
/// <item><description><b>Has the table</b> (the normal case — every database created or migrated
/// by this app or by the upstream WPF app) — it is dropped, along with its two indexes, which
/// SQLite removes with the table.</description></item>
/// <item><description><b>Does not have the table</b> — a database hand-modified, restored from a
/// partial dump, or baselined out of <c>EnsureCreated()</c> by
/// <c>DatabaseMigrator.SeedMigrationHistoryIfNeeded</c> against a model that no longer declares it
/// — the <c>IF EXISTS</c> guard makes this a no-op instead of a migration failure that would leave
/// the user unable to open their logs at all. Upstream added the same guard in response to its own
/// review; it is the single most important line here.</description></item>
/// <item><description><b>Interrupted part-way</b> — <c>Migrate()</c> runs each migration in a
/// transaction, so an interruption rolls this back and the row is never written to
/// <c>__EFMigrationsHistory</c>; the next launch retries from a database that still has the table.
/// Retrying after a partial success is safe for the same reason the missing-table case is: the
/// statement is idempotent. <c>DatabaseMigrator</c> also copies the file to
/// <c>*.migration-backup</c> before migrating and restores it if migration throws.</description>
/// </item>
/// </list>
/// <para><b>What <see cref="Down"/> can and cannot do.</b> It recreates the table with the exact
/// column, key, foreign-key and index shape <see cref="InitialSQLiteMigration"/> gave it, so an
/// older build of the app finds the schema it expects and starts. It does <b>not</b> restore rows:
/// <c>DROP TABLE</c> is not reversible, and no row data is retained anywhere. That loses nothing in
/// practice — the table has no writer in any released version, so it is empty — but the down
/// migration is a schema rollback, not a data rollback, and should not be read as one.</para>
/// <para><b>Deliberately narrower than upstream.</b> Upstream generated its migration with
/// <c>dotnet ef migrations add</c>, which diffed the whole model and swept in a rename of the
/// <c>Samples</c> composite index (<c>IX_Samples_LoggingSessionID_TimestampTicks</c> ->
/// <c>IX_Samples_SessionTime</c>) plus a drop of <c>IX_Samples_LoggingSessionID</c>. That drift
/// exists in this repo too — <c>LoggingContext</c> asks for <c>IX_Samples_SessionTime</c> while
/// <c>AddSamplesSessionTimeIndex</c> creates the longer name — and it is deliberately left alone
/// here. Renaming an index on a user's database is a change with its own risk profile and its own
/// justification, and it does not belong inside a dead-code deletion. The model snapshot is
/// therefore edited by hand to remove only the <c>Channel</c> entity, which keeps the pre-existing
/// index drift exactly as visible (and as suppressed, via
/// <c>RelationalEventId.PendingModelChangesWarning</c>) as it is today.</para>
/// </remarks>
public partial class DropChannelTable : Migration
{
    /// <inheritdoc />
    // @port: Daqifi.Desktop.Migrations.DropChannelTable.Up
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Raw SQL rather than migrationBuilder.DropTable("Channel"): DropTable emits an
        // unguarded DROP TABLE, which throws on a database that does not have the table and
        // takes the whole migration — and with it the user's access to their logs — down with
        // it. See the remarks on this class for the three database shapes this has to survive.
        migrationBuilder.Sql("DROP TABLE IF EXISTS \"Channel\";");
    }

    /// <inheritdoc />
    // @port: Daqifi.Desktop.Migrations.DropChannelTable.Down
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Unguarded CreateTable on the way back, on purpose. Up has to tolerate databases it did
        // not create; Down only ever runs against a database this migration has already dropped
        // the table from, so a Channel table already standing here means something else made it
        // and silently merging with it would be worse than failing.
        //
        // This restores the SHAPE the initial migration created, not the rows: nothing retains
        // them, and nothing ever wrote any.
        migrationBuilder.CreateTable(
            name: "Channel",
            columns: table => new
            {
                ID = table.Column<int>(type: "INTEGER", nullable: false)
                    .Annotation("Sqlite:Autoincrement", true),
                Name = table.Column<string>(type: "TEXT", nullable: false),
                Index = table.Column<int>(type: "INTEGER", nullable: false),
                OutputValue = table.Column<double>(type: "REAL", nullable: false),
                Type = table.Column<int>(type: "INTEGER", nullable: false),
                Direction = table.Column<int>(type: "INTEGER", nullable: false),
                TypeString = table.Column<string>(type: "TEXT", nullable: false),
                ScaleExpression = table.Column<string>(type: "TEXT", nullable: false),
                IsBidirectional = table.Column<bool>(type: "INTEGER", nullable: false),
                IsOutput = table.Column<bool>(type: "INTEGER", nullable: false),
                HasAdc = table.Column<bool>(type: "INTEGER", nullable: false),
                IsActive = table.Column<bool>(type: "INTEGER", nullable: false),
                IsDigital = table.Column<bool>(type: "INTEGER", nullable: false),
                IsAnalog = table.Column<bool>(type: "INTEGER", nullable: false),
                IsDigitalOn = table.Column<bool>(type: "INTEGER", nullable: false),
                IsScalingActive = table.Column<bool>(type: "INTEGER", nullable: false),
                HasValidExpression = table.Column<bool>(type: "INTEGER", nullable: false),
                ActiveSampleID = table.Column<int>(type: "INTEGER", nullable: false),
                IsVisible = table.Column<bool>(type: "INTEGER", nullable: false),
                DeviceName = table.Column<string>(type: "TEXT", nullable: false),
                DeviceSerialNo = table.Column<string>(type: "TEXT", nullable: false),
                LoggingSessionID = table.Column<int>(type: "INTEGER", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_Channel", x => x.ID);
                table.ForeignKey(
                    name: "FK_Channel_Samples_ActiveSampleID",
                    column: x => x.ActiveSampleID,
                    principalTable: "Samples",
                    principalColumn: "ID",
                    onDelete: ReferentialAction.Cascade);
                table.ForeignKey(
                    name: "FK_Channel_Sessions_LoggingSessionID",
                    column: x => x.LoggingSessionID,
                    principalTable: "Sessions",
                    principalColumn: "ID");
            });

        migrationBuilder.CreateIndex(
            name: "IX_Channel_ActiveSampleID",
            table: "Channel",
            column: "ActiveSampleID");

        migrationBuilder.CreateIndex(
            name: "IX_Channel_LoggingSessionID",
            table: "Channel",
            column: "LoggingSessionID");
    }
}
