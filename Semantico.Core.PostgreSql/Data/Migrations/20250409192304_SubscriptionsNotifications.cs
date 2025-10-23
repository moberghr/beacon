using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class SubscriptionsNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "include_attachment",
                schema: "semantico",
                table: "subscriptions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "max_rows",
                schema: "semantico",
                table: "subscriptions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "show_query",
                schema: "semantico",
                table: "subscriptions",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "notification_status",
                schema: "semantico",
                table: "query_execution_history",
                type: "integer",
                nullable: false,
                defaultValue: 0);
            
            // Update the notification_status based on the existing notification_sent value
            migrationBuilder.Sql(@"
                UPDATE semantico.query_execution_history 
                SET notification_status = CASE 
                    WHEN notification_sent = true THEN 2  -- NotificationSent (2)
                    ELSE 1  -- Created (1)
                END
            ");
            
            migrationBuilder.DropColumn(
                name: "notification_sent",
                schema: "semantico",
                table: "query_execution_history");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "include_attachment",
                schema: "semantico",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "max_rows",
                schema: "semantico",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "show_query",
                schema: "semantico",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "notification_status",
                schema: "semantico",
                table: "query_execution_history");

            migrationBuilder.AddColumn<bool>(
                name: "notification_sent",
                schema: "semantico",
                table: "query_execution_history",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }
    }
}
