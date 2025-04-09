using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class UpdateNotificationSentToStatus : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // First, add the new NotificationStatus column with default value of 0 (Created)
            migrationBuilder.AddColumn<int>(
                name: "NotificationStatus",
                table: "QueryExecutionHistory",
                type: "int",
                nullable: false,
                defaultValue: 0);

            // Update the NotificationStatus based on the existing NotificationSent value
            migrationBuilder.Sql(@"
                UPDATE QueryExecutionHistory 
                SET NotificationStatus = CASE 
                    WHEN NotificationSent = 1 THEN 1  -- NotificationSent
                    ELSE 0  -- Created
                END
            ");

            // Drop the old NotificationSent column
            migrationBuilder.DropColumn(
                name: "NotificationSent",
                table: "QueryExecutionHistory");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Add back the NotificationSent column
            migrationBuilder.AddColumn<bool>(
                name: "NotificationSent",
                table: "QueryExecutionHistory",
                type: "bit",
                nullable: false,
                defaultValue: false);

            // Convert the NotificationStatus back to NotificationSent
            migrationBuilder.Sql(@"
                UPDATE QueryExecutionHistory 
                SET NotificationSent = CASE 
                    WHEN NotificationStatus = 1 THEN 1  -- NotificationSent
                    ELSE 0  -- All other statuses are not sent
                END
            ");

            // Drop the NotificationStatus column
            migrationBuilder.DropColumn(
                name: "NotificationStatus",
                table: "QueryExecutionHistory");
        }
    }
}