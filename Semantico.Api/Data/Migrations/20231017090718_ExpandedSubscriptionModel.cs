using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Api.Migrations
{
    /// <inheritdoc />
    public partial class ExpandedSubscriptionModel : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "notification_type",
                schema: "semantico",
                table: "subscriptions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "recipient",
                schema: "semantico",
                table: "subscriptions",
                type: "text",
                nullable: false,
                defaultValue: "");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "notification_type",
                schema: "semantico",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "recipient",
                schema: "semantico",
                table: "subscriptions");
        }
    }
}
