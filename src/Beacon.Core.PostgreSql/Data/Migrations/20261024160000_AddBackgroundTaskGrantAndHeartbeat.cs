using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBackgroundTaskGrantAndHeartbeat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "heartbeat_time",
                schema: "beacon",
                table: "background_tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "permission_source",
                schema: "beacon",
                table: "background_tasks",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "requester_stamp_hash",
                schema: "beacon",
                table: "background_tasks",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "heartbeat_time",
                schema: "beacon",
                table: "background_tasks");

            migrationBuilder.DropColumn(
                name: "permission_source",
                schema: "beacon",
                table: "background_tasks");

            migrationBuilder.DropColumn(
                name: "requester_stamp_hash",
                schema: "beacon",
                table: "background_tasks");
        }
    }
}
