using Jellyfin.Database.Implementations;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Jellyfin.Plugin.Database.Npgsql.Migrations;

[DbContext(typeof(JellyfinDbContext))]
[Migration("20261008160000_ForcedNonTransactionalFailure")]
internal sealed class ForcedFailureMigration : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "CREATE TABLE \"FeasibilityFailureMarker\" (\"Id\" integer NOT NULL)",
            suppressTransaction: true);
        migrationBuilder.Sql("SELECT 1 / 0", suppressTransaction: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("FeasibilityFailureMarker");
    }
}
