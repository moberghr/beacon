using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSelfLearningTier2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "EnableReplayVerification",
                schema: "beacon",
                table: "McpSettings",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "LearningReplayMinFlips",
                schema: "beacon",
                table: "McpSettings",
                type: "int",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "SupersededAt",
                schema: "beacon",
                table: "McpLearnedPatterns",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "LastVerifiedAt",
                schema: "beacon",
                table: "McpLearnedPatterns",
                type: "datetime2",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "EnableReplayVerification",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "LearningReplayMinFlips",
                schema: "beacon",
                table: "McpSettings");

            migrationBuilder.DropColumn(
                name: "SupersededAt",
                schema: "beacon",
                table: "McpLearnedPatterns");

            migrationBuilder.DropColumn(
                name: "LastVerifiedAt",
                schema: "beacon",
                table: "McpLearnedPatterns");
        }
    }
}
