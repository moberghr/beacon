using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQueryMcpTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "McpToolDescription",
                schema: "beacon",
                table: "Queries",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "McpToolEnabled",
                schema: "beacon",
                table: "Queries",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "McpToolName",
                schema: "beacon",
                table: "Queries",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Queries_McpToolName",
                schema: "beacon",
                table: "Queries",
                column: "McpToolName",
                unique: true,
                filter: "[McpToolName] IS NOT NULL AND [ArchivedTime] IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Queries_McpToolName",
                schema: "beacon",
                table: "Queries");

            migrationBuilder.DropColumn(
                name: "McpToolDescription",
                schema: "beacon",
                table: "Queries");

            migrationBuilder.DropColumn(
                name: "McpToolEnabled",
                schema: "beacon",
                table: "Queries");

            migrationBuilder.DropColumn(
                name: "McpToolName",
                schema: "beacon",
                table: "Queries");
        }
    }
}
