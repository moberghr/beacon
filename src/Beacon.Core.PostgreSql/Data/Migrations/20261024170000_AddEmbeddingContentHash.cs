using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddEmbeddingContentHash : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "content_hash",
                schema: "beacon",
                table: "mcp_embeddings",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "ix_notifications_sent_at",
                schema: "beacon",
                table: "notifications",
                column: "sent_at");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ix_notifications_sent_at",
                schema: "beacon",
                table: "notifications");

            migrationBuilder.DropColumn(
                name: "content_hash",
                schema: "beacon",
                table: "mcp_embeddings");
        }
    }
}
