using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSelfLearningTier2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "enable_replay_verification",
                table: "mcp_settings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "learning_replay_min_flips",
                table: "mcp_settings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<DateTime>(
                name: "superseded_at",
                table: "mcp_learned_patterns",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "last_verified_at",
                table: "mcp_learned_patterns",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "enable_replay_verification",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "learning_replay_min_flips",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "superseded_at",
                table: "mcp_learned_patterns");

            migrationBuilder.DropColumn(
                name: "last_verified_at",
                table: "mcp_learned_patterns");
        }
    }
}
