using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Jellyfin.Plugin.Database.Npgsql.Migrations;

/// <summary>
/// Test-only migration that verifies Jellyfin invokes provider rollback after a committed failure.
/// </summary>
[DbContext(typeof(JellyfinDbContext))]
[Migration("20261008160000_ForcedNonTransactionalFailure")]
internal sealed class ForcedFailureMigration : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "CREATE TABLE \"FeasibilityFailureMarker\" (\"Id\" integer NOT NULL)",
            suppressTransaction: true);
        migrationBuilder.Sql("SELECT 1 / 0", suppressTransaction: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("FeasibilityFailureMarker");
    }
}
