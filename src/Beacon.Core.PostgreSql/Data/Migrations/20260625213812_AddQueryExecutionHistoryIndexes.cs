using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQueryExecutionHistoryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_query_execution_history_subscription_id",
                table: "query_execution_history");

            migrationBuilder.CreateIndex(
                name: "ix_query_execution_history_created_time",
                table: "query_execution_history",
                column: "created_time");

            migrationBuilder.CreateIndex(
                name: "ix_query_execution_history_notification_status_created_time",
                table: "query_execution_history",
                columns: new[] { "notification_status", "created_time" });

            migrationBuilder.CreateIndex(
                name: "ix_query_execution_history_subscription_id_created_time",
                table: "query_execution_history",
                columns: new[] { "subscription_id", "created_time" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_query_execution_history_created_time",
                table: "query_execution_history");

            migrationBuilder.DropIndex(
                name: "ix_query_execution_history_notification_status_created_time",
                table: "query_execution_history");

            migrationBuilder.DropIndex(
                name: "ix_query_execution_history_subscription_id_created_time",
                table: "query_execution_history");

            migrationBuilder.CreateIndex(
                name: "ix_query_execution_history_subscription_id",
                table: "query_execution_history",
                column: "subscription_id");
        }
    }
}
