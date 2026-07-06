using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class FixUsernameUniqueIndexAndSampleDefault : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_user_name_archived_time",
                table: "users");

            migrationBuilder.AlterColumn<bool>(
                name: "enable_sample_value_collection",
                table: "mcp_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true,
                oldClrType: typeof(bool),
                oldType: "boolean");

            migrationBuilder.CreateIndex(
                name: "ix_users_user_name",
                table: "users",
                column: "user_name",
                unique: true,
                filter: "archived_time IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_users_user_name",
                table: "users");

            migrationBuilder.AlterColumn<bool>(
                name: "enable_sample_value_collection",
                table: "mcp_settings",
                type: "boolean",
                nullable: false,
                oldClrType: typeof(bool),
                oldType: "boolean",
                oldDefaultValue: true);

            migrationBuilder.CreateIndex(
                name: "ix_users_user_name_archived_time",
                table: "users",
                columns: new[] { "user_name", "archived_time" });
        }
    }
}
