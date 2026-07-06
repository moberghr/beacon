using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQueryExecutionHistoryIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QueryExecutionHistory_SubscriptionId",
                schema: "beacon",
                table: "QueryExecutionHistory");

            migrationBuilder.CreateIndex(
                name: "IX_QueryExecutionHistory_CreatedTime",
                schema: "beacon",
                table: "QueryExecutionHistory",
                column: "CreatedTime");

            migrationBuilder.CreateIndex(
                name: "IX_QueryExecutionHistory_NotificationStatus_CreatedTime",
                schema: "beacon",
                table: "QueryExecutionHistory",
                columns: new[] { "NotificationStatus", "CreatedTime" });

            migrationBuilder.CreateIndex(
                name: "IX_QueryExecutionHistory_SubscriptionId_CreatedTime",
                schema: "beacon",
                table: "QueryExecutionHistory",
                columns: new[] { "SubscriptionId", "CreatedTime" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_QueryExecutionHistory_CreatedTime",
                schema: "beacon",
                table: "QueryExecutionHistory");

            migrationBuilder.DropIndex(
                name: "IX_QueryExecutionHistory_NotificationStatus_CreatedTime",
                schema: "beacon",
                table: "QueryExecutionHistory");

            migrationBuilder.DropIndex(
                name: "IX_QueryExecutionHistory_SubscriptionId_CreatedTime",
                schema: "beacon",
                table: "QueryExecutionHistory");

            migrationBuilder.CreateIndex(
                name: "IX_QueryExecutionHistory_SubscriptionId",
                schema: "beacon",
                table: "QueryExecutionHistory",
                column: "SubscriptionId");
        }
    }
}
