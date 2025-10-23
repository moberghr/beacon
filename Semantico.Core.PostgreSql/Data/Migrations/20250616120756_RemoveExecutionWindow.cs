using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Semantico.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveExecutionWindow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "execution_window_end_hour",
                schema: "semantico",
                table: "subscriptions");

            migrationBuilder.DropColumn(
                name: "execution_window_start_hour",
                schema: "semantico",
                table: "subscriptions");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "execution_window_end_hour",
                schema: "semantico",
                table: "subscriptions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "execution_window_start_hour",
                schema: "semantico",
                table: "subscriptions",
                type: "integer",
                nullable: true);
        }
    }
}
