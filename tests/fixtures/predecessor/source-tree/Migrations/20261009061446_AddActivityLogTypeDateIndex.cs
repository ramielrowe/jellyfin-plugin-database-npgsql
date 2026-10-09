using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Jellyfin.Plugin.Database.Npgsql.Migrations
{
    /// <inheritdoc />
    public partial class AddActivityLogTypeDateIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_ActivityLogs_Npgsql_Type_DateCreated",
                table: "ActivityLogs",
                columns: new[] { "Type", "DateCreated" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ActivityLogs_Npgsql_Type_DateCreated",
                table: "ActivityLogs");
        }
    }
}
