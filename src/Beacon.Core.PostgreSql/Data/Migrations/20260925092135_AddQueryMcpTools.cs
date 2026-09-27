using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQueryMcpTools : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "mcp_tool_description",
                schema: "beacon",
                table: "queries",
                type: "character varying(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "mcp_tool_enabled",
                schema: "beacon",
                table: "queries",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "mcp_tool_name",
                schema: "beacon",
                table: "queries",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_queries_mcp_tool_name",
                schema: "beacon",
                table: "queries",
                column: "mcp_tool_name",
                unique: true,
                filter: "mcp_tool_name IS NOT NULL AND archived_time IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_queries_mcp_tool_name",
                schema: "beacon",
                table: "queries");

            migrationBuilder.DropColumn(
                name: "mcp_tool_description",
                schema: "beacon",
                table: "queries");

            migrationBuilder.DropColumn(
                name: "mcp_tool_enabled",
                schema: "beacon",
                table: "queries");

            migrationBuilder.DropColumn(
                name: "mcp_tool_name",
                schema: "beacon",
                table: "queries");
        }
    }
}
