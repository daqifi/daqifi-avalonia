// Downstream addition — no upstream counterpart, so no `// @port:` marker.

using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Daqifi.Desktop.Migrations;

/// <summary>
/// Adds <c>Sessions.Status</c>, the column that records whether a session is finished, is being
/// written by an SD card import, or was abandoned by one that failed. See
/// <see cref="Daqifi.Desktop.Logger.SessionStatus"/>.
/// </summary>
/// <remarks>
/// Non-nullable with a zero default, so every row already in the database becomes
/// <c>SessionStatus.Complete</c> — which is what they are: nothing before this column could write
/// a partial session and then get the chance to say so.
/// </remarks>
public partial class AddSessionStatus : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "Status",
            table: "Sessions",
            type: "INTEGER",
            nullable: false,
            defaultValue: 0);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "Status",
            table: "Sessions");
    }
}
