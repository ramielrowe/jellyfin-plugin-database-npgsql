using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.Database.Npgsql.Migrations
{
    /// <inheritdoc />
    public partial class AddUuidMinMaxAggregates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(NpgsqlUuidAggregates.InstallSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(NpgsqlUuidAggregates.UninstallSql);
        }
    }
}
