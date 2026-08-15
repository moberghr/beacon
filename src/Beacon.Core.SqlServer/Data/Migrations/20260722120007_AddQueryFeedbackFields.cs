using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddQueryFeedbackFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "FeedbackNote",
                schema: "beacon",
                table: "McpQuerySignals",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "UserCorrectedSql",
                schema: "beacon",
                table: "McpQuerySignals",
                type: "nvarchar(max)",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "UserVerdict",
                schema: "beacon",
                table: "McpQuerySignals",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "FeedbackNote",
                schema: "beacon",
                table: "McpQuerySignals");

            migrationBuilder.DropColumn(
                name: "UserCorrectedSql",
                schema: "beacon",
                table: "McpQuerySignals");

            migrationBuilder.DropColumn(
                name: "UserVerdict",
                schema: "beacon",
                table: "McpQuerySignals");
        }
    }
}
