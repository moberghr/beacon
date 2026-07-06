using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class TaskAssignSnoozePriorityWatchersSubscriptionSla : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "sla_hours",
                table: "subscriptions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "assignee_user_id",
                table: "query_tasks",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "priority",
                table: "query_tasks",
                type: "integer",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<DateTime>(
                name: "snoozed_until",
                table: "query_tasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "task_watchers",
                columns: table => new
                {
                    query_task_id = table.Column<int>(type: "integer", nullable: false),
                    user_id = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_task_watchers", x => new { x.query_task_id, x.user_id });
                    table.ForeignKey(
                        name: "fk_task_watchers_query_tasks_query_task_id",
                        column: x => x.query_task_id,
                        principalTable: "query_tasks",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_query_tasks_assignee_user_id",
                table: "query_tasks",
                column: "assignee_user_id");

            migrationBuilder.CreateIndex(
                name: "ix_query_tasks_priority",
                table: "query_tasks",
                column: "priority");

            migrationBuilder.CreateIndex(
                name: "ix_query_tasks_snoozed_until",
                table: "query_tasks",
                column: "snoozed_until");

            migrationBuilder.CreateIndex(
                name: "ix_task_watchers_user_id",
                table: "task_watchers",
                column: "user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "task_watchers");

            migrationBuilder.DropIndex(
                name: "ix_query_tasks_assignee_user_id",
                table: "query_tasks");

            migrationBuilder.DropIndex(
                name: "ix_query_tasks_priority",
                table: "query_tasks");

            migrationBuilder.DropIndex(
                name: "ix_query_tasks_snoozed_until",
                table: "query_tasks");

            migrationBuilder.DropColumn(
                name: "sla_hours",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "assignee_user_id",
                table: "query_tasks");

            migrationBuilder.DropColumn(
                name: "priority",
                table: "query_tasks");

            migrationBuilder.DropColumn(
                name: "snoozed_until",
                table: "query_tasks");
        }
    }
}
