using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpSelfLearningSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableSelfConsistency",
                schema: "beacon",
                table: "McpSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SelfConsistencyCandidateCount",
                schema: "beacon",
                table: "McpSettings",
                type: "int",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<bool>(
                name: "EnableEvalJudge",
                schema: "beacon",
                table: "McpSettings",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "EnableSemanticRetrieval",
                schema: "beacon",
                table: "McpSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "ExemplarTopK",
                schema: "beacon",
                table: "McpSettings",
                type: "int",
                nullable: false,
                defaultValue: 4);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnableSelfConsistency",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "SelfConsistencyCandidateCount",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "EnableEvalJudge",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "EnableSemanticRetrieval",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "ExemplarTopK",
                schema: "beacon",
                table: "McpSettings");
        }
    }
}
