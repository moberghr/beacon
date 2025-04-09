using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSubscriptionSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IncludeAttachment",
                table: "Subscriptions",
                type: "bit",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxRows",
                table: "Subscriptions",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowQuery",
                table: "Subscriptions",
                type: "bit",
                nullable: false,
                defaultValue: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IncludeAttachment",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "MaxRows",
                table: "Subscriptions");

            migrationBuilder.DropColumn(
                name: "ShowQuery",
                table: "Subscriptions");
        }
    }
}