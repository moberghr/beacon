using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixUsernameUniqueIndexAndSampleDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_UserName_ArchivedTime",
                schema: "beacon",
                table: "Users");

            migrationBuilder.AlterColumn<bool>(
                name: "EnableSampleValueCollection",
                schema: "beacon",
                table: "McpSettings",
                type: "bit",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "bit");

            migrationBuilder.CreateIndex(
                name: "IX_Users_UserName",
                schema: "beacon",
                table: "Users",
                column: "UserName",
                unique: true,
                filter: "[ArchivedTime] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_UserName",
                schema: "beacon",
                table: "Users");

            migrationBuilder.AlterColumn<bool>(
                name: "EnableSampleValueCollection",
                schema: "beacon",
                table: "McpSettings",
                type: "bit",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "bit",
                oldDefaultValue: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_UserName_ArchivedTime",
                schema: "beacon",
                table: "Users",
                columns: new[] { "UserName", "ArchivedTime" });
        }
    }
}
