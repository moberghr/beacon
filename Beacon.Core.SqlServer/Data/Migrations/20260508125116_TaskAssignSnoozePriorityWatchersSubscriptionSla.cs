using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class TaskAssignSnoozePriorityWatchersSubscriptionSla : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "SlaHours",
                schema: "beacon",
                table: "Subscriptions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AssigneeUserId",
                schema: "beacon",
                table: "QueryTasks",
                type: "nvarchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Priority",
                schema: "beacon",
                table: "QueryTasks",
                type: "int",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<DateTime>(
                name: "SnoozedUntil",
                schema: "beacon",
                table: "QueryTasks",
                type: "datetime2",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TaskWatchers",
                schema: "beacon",
                columns: table => new
                {
                    QueryTaskId = table.Column<int>(type: "int", nullable: false),
                    UserId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    CreatedTime = table.Column<DateTime>(type: "datetime2", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TaskWatchers", x => new { x.QueryTaskId, x.UserId });
                    table.ForeignKey(
                        name: "FK_TaskWatchers_QueryTasks_QueryTaskId",
                        column: x => x.QueryTaskId,
                        principalSchema: "beacon",
                        principalTable: "QueryTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QueryTasks_AssigneeUserId",
                schema: "beacon",
                table: "QueryTasks",
                column: "AssigneeUserId");

            migrationBuilder.CreateIndex(
                name: "IX_QueryTasks_Priority",
                schema: "beacon",
                table: "QueryTasks",
                column: "Priority");

            migrationBuilder.CreateIndex(
                name: "IX_QueryTasks_SnoozedUntil",
                schema: "beacon",
                table: "QueryTasks",
                column: "SnoozedUntil");

            migrationBuilder.CreateIndex(
                name: "IX_TaskWatchers_UserId",
                schema: "beacon",
                table: "TaskWatchers",
                column: "UserId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TaskWatchers",
                schema: "beacon");

            migrationBuilder.DropIndex(
                name: "IX_QueryTasks_AssigneeUserId",
                schema: "beacon",
                table: "QueryTasks");

            migrationBuilder.DropIndex(
                name: "IX_QueryTasks_Priority",
                schema: "beacon",
                table: "QueryTasks");

            migrationBuilder.DropIndex(
                name: "IX_QueryTasks_SnoozedUntil",
                schema: "beacon",
                table: "QueryTasks");

            migrationBuilder.DropColumn(
                name: "SlaHours",
                schema: "beacon",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "AssigneeUserId",
                schema: "beacon",
                table: "QueryTasks");

            migrationBuilder.DropColumn(
                name: "Priority",
                schema: "beacon",
                table: "QueryTasks");

            migrationBuilder.DropColumn(
                name: "SnoozedUntil",
                schema: "beacon",
                table: "QueryTasks");
        }
    }
}
