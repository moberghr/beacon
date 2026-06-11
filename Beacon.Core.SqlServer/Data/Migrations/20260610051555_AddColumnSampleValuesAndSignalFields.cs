using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddColumnSampleValuesAndSignalFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableSampleValueCollection",
                schema: "beacon",
                table: "McpSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "DryRunError",
                schema: "beacon",
                table: "McpQuerySignals",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "DryRunFailed",
                schema: "beacon",
                table: "McpQuerySignals",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EmptyResultRetryAttempted",
                schema: "beacon",
                table: "McpQuerySignals",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SampleValues",
                schema: "beacon",
                table: "ColumnMetadata",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnableSampleValueCollection",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "DryRunError",
                schema: "beacon",
                table: "McpQuerySignals");

            migrationBuilder.DropColumn(
                name: "DryRunFailed",
                schema: "beacon",
                table: "McpQuerySignals");

            migrationBuilder.DropColumn(
                name: "EmptyResultRetryAttempted",
                schema: "beacon",
                table: "McpQuerySignals");

            migrationBuilder.DropColumn(
                name: "SampleValues",
                schema: "beacon",
                table: "ColumnMetadata");
        }
    }
}
