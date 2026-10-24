using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddBackgroundTaskGrantAndHeartbeat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "HeartbeatTime",
                schema: "beacon",
                table: "BackgroundTasks",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PermissionSource",
                schema: "beacon",
                table: "BackgroundTasks",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RequesterStampHash",
                schema: "beacon",
                table: "BackgroundTasks",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HeartbeatTime",
                schema: "beacon",
                table: "BackgroundTasks");

            migrationBuilder.DropColumn(
                name: "PermissionSource",
                schema: "beacon",
                table: "BackgroundTasks");

            migrationBuilder.DropColumn(
                name: "RequesterStampHash",
                schema: "beacon",
                table: "BackgroundTasks");
        }
    }
}
