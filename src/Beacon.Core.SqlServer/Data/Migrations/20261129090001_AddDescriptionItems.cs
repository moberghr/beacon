using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDescriptionItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableDocChunksInAsk",
                schema: "beacon",
                table: "McpSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EnableDocChunksInAsk",
                schema: "beacon",
                table: "McpProjectSettings",
                type: "bit",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GlobalInstruction",
                schema: "beacon",
                table: "McpProjectSettings",
                type: "nvarchar(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "GeneratedByModel",
                schema: "beacon",
                table: "McpDocumentationPatches",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "McpDescriptionItems",
                schema: "beacon",
                columns: table => new
                {
                    Id = table.Column<int>(type: "int", nullable: false)
                        .Annotation("SqlServer:Identity", "1, 1"),
                    DataSourceId = table.Column<int>(type: "int", nullable: false),
                    SchemaName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    TableName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ColumnName = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Source = table.Column<int>(type: "int", nullable: false),
                    WrittenAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    WrittenByUserId = table.Column<int>(type: "int", nullable: true),
                    GeneratedByModel = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    SourcePatchId = table.Column<int>(type: "int", nullable: true),
                    StructureHash = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    StaleSince = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_McpDescriptionItems", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_McpDescriptionItems_DataSourceId_SchemaName_TableName_ColumnName",
                schema: "beacon",
                table: "McpDescriptionItems",
                columns: new[] { "DataSourceId", "SchemaName", "TableName", "ColumnName" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "McpDescriptionItems",
                schema: "beacon");

            migrationBuilder.DropColumn(
                name: "EnableDocChunksInAsk",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "EnableDocChunksInAsk",
                schema: "beacon",
                table: "McpProjectSettings");

            migrationBuilder.DropColumn(
                name: "GlobalInstruction",
                schema: "beacon",
                table: "McpProjectSettings");

            migrationBuilder.DropColumn(
                name: "GeneratedByModel",
                schema: "beacon",
                table: "McpDocumentationPatches");
        }
    }
}
