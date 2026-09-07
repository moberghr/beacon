using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAskCorrectnessGrounding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableSemanticLint",
                schema: "beacon",
                table: "McpSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "EnableValueGrounding",
                schema: "beacon",
                table: "McpSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "SelfConsistencyMinTables",
                schema: "beacon",
                table: "McpSettings",
                type: "int",
                nullable: false,
                defaultValue: 2);

            migrationBuilder.AddColumn<int>(
                name: "ValueGroundingMaxProbes",
                schema: "beacon",
                table: "McpSettings",
                type: "int",
                nullable: false,
                defaultValue: 12);

            migrationBuilder.AddColumn<bool>(
                name: "SampleValuesComplete",
                schema: "beacon",
                table: "ColumnMetadata",
                type: "bit",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnableSemanticLint",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "EnableValueGrounding",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "SelfConsistencyMinTables",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "ValueGroundingMaxProbes",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "SampleValuesComplete",
                schema: "beacon",
                table: "ColumnMetadata");
        }
    }
}
