using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddGoldenExemplarSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_McpEmbeddings_DataSourceId_OwnerType_OwnerId",
                schema: "beacon",
                table: "McpEmbeddings");

            migrationBuilder.AddColumn<bool>(
                name: "EnableGoldenExemplars",
                schema: "beacon",
                table: "McpSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "GoldenExemplarBudgetChars",
                schema: "beacon",
                table: "McpSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "GoldenExemplarTopK",
                schema: "beacon",
                table: "McpSettings",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_McpEmbeddings_DataSourceId_OwnerType_OwnerId",
                schema: "beacon",
                table: "McpEmbeddings",
                columns: new[] { "DataSourceId", "OwnerType", "OwnerId" },
                unique: true,
                filter: "[DataSourceId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_McpEmbeddings_DataSourceId_OwnerType_OwnerId",
                schema: "beacon",
                table: "McpEmbeddings");

            migrationBuilder.DropColumn(
                name: "EnableGoldenExemplars",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "GoldenExemplarBudgetChars",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "GoldenExemplarTopK",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.CreateIndex(
                name: "IX_McpEmbeddings_DataSourceId_OwnerType_OwnerId",
                schema: "beacon",
                table: "McpEmbeddings",
                columns: new[] { "DataSourceId", "OwnerType", "OwnerId" },
                unique: true);
        }
    }
}
