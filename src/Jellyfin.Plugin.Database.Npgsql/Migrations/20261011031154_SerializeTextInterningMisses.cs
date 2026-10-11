using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.Database.Npgsql.Migrations
{
    /// <inheritdoc />
    public partial class SerializeTextInterningMisses : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(NpgsqlTextInterning.InstallDeadlockSafeInternFunctionSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(NpgsqlTextInterning.RestorePredecessorInternFunctionSql);
        }
    }
}
