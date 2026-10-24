using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddExecutionLogStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "TriggeredByUserId",
                schema: "beacon",
                table: "QueryExecutionHistory",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "CompletedTime",
                schema: "beacon",
                table: "ManualQueryExecutionLogs",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "Status",
                schema: "beacon",
                table: "ManualQueryExecutionLogs",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TriggeredByUserId",
                schema: "beacon",
                table: "ManualQueryExecutionLogs",
                type: "int",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ManualQueryExecutionLogs_TriggeredByUserId_CreatedTime",
                schema: "beacon",
                table: "ManualQueryExecutionLogs",
                columns: new[] { "TriggeredByUserId", "CreatedTime" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ManualQueryExecutionLogs_TriggeredByUserId_CreatedTime",
                schema: "beacon",
                table: "ManualQueryExecutionLogs");

            migrationBuilder.DropColumn(
                name: "TriggeredByUserId",
                schema: "beacon",
                table: "QueryExecutionHistory");

            migrationBuilder.DropColumn(
                name: "CompletedTime",
                schema: "beacon",
                table: "ManualQueryExecutionLogs");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "beacon",
                table: "ManualQueryExecutionLogs");

            migrationBuilder.DropColumn(
                name: "TriggeredByUserId",
                schema: "beacon",
                table: "ManualQueryExecutionLogs");
        }
    }
}
