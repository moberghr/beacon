using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionLogStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "triggered_by_user_id",
                schema: "beacon",
                table: "query_execution_history",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "completed_time",
                schema: "beacon",
                table: "manual_query_execution_logs",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "status",
                schema: "beacon",
                table: "manual_query_execution_logs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "triggered_by_user_id",
                schema: "beacon",
                table: "manual_query_execution_logs",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_manual_query_execution_logs_triggered_by_user_id_created_ti",
                schema: "beacon",
                table: "manual_query_execution_logs",
                columns: new[] { "triggered_by_user_id", "created_time" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_manual_query_execution_logs_triggered_by_user_id_created_ti",
                schema: "beacon",
                table: "manual_query_execution_logs");

            migrationBuilder.DropColumn(
                name: "triggered_by_user_id",
                schema: "beacon",
                table: "query_execution_history");

            migrationBuilder.DropColumn(
                name: "completed_time",
                schema: "beacon",
                table: "manual_query_execution_logs");

            migrationBuilder.DropColumn(
                name: "status",
                schema: "beacon",
                table: "manual_query_execution_logs");

            migrationBuilder.DropColumn(
                name: "triggered_by_user_id",
                schema: "beacon",
                table: "manual_query_execution_logs");
        }
    }
}
