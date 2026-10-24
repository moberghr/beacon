using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddingContentHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ContentHash",
                schema: "beacon",
                table: "McpEmbeddings",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Notifications_SentAt",
                schema: "beacon",
                table: "Notifications",
                column: "SentAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Notifications_SentAt",
                schema: "beacon",
                table: "Notifications");

            migrationBuilder.DropColumn(
                name: "ContentHash",
                schema: "beacon",
                table: "McpEmbeddings");
        }
    }
}
