using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddColumnSampleValuesAndSignalFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "enable_sample_value_collection",
                table: "mcp_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "dry_run_error",
                table: "mcp_query_signals",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "dry_run_failed",
                table: "mcp_query_signals",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "empty_result_retry_attempted",
                table: "mcp_query_signals",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "sample_values",
                table: "column_metadata",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "enable_sample_value_collection",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "dry_run_error",
                table: "mcp_query_signals");

            migrationBuilder.DropColumn(
                name: "dry_run_failed",
                table: "mcp_query_signals");

            migrationBuilder.DropColumn(
                name: "empty_result_retry_attempted",
                table: "mcp_query_signals");

            migrationBuilder.DropColumn(
                name: "sample_values",
                table: "column_metadata");
        }
    }
}
