using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddStoreResultsAndNotificationResults : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "store_results",
                schema: "semantico",
                table: "subscriptions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "results",
                schema: "semantico",
                table: "notifications",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "store_results",
                schema: "semantico",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "results",
                schema: "semantico",
                table: "notifications");
        }
    }
}
