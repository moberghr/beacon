using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSignalProvenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "feedback_reason",
                schema: "beacon",
                table: "mcp_query_signals",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "is_edited_rerun",
                schema: "beacon",
                table: "mcp_query_signals",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "parent_signal_id",
                schema: "beacon",
                table: "mcp_query_signals",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "mcp_signal_context_items",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    signal_id = table.Column<int>(type: "integer", nullable: false),
                    kind = table.Column<int>(type: "integer", nullable: false),
                    item_id = table.Column<int>(type: "integer", nullable: false),
                    rank = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_signal_context_items", x => x.id);
                    table.ForeignKey(
                        name: "fk_mcp_signal_context_items_mcp_query_signals_signal_id",
                        column: x => x.signal_id,
                        principalSchema: "beacon",
                        principalTable: "mcp_query_signals",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_query_signals_parent_signal_id",
                schema: "beacon",
                table: "mcp_query_signals",
                column: "parent_signal_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_signal_context_items_kind_item_id",
                schema: "beacon",
                table: "mcp_signal_context_items",
                columns: new[] { "kind", "item_id" });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_signal_context_items_signal_id",
                schema: "beacon",
                table: "mcp_signal_context_items",
                column: "signal_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_signal_context_items",
                schema: "beacon");

            migrationBuilder.DropIndex(
                name: "ix_mcp_query_signals_parent_signal_id",
                schema: "beacon",
                table: "mcp_query_signals");

            migrationBuilder.DropColumn(
                name: "feedback_reason",
                schema: "beacon",
                table: "mcp_query_signals");

            migrationBuilder.DropColumn(
                name: "is_edited_rerun",
                schema: "beacon",
                table: "mcp_query_signals");

            migrationBuilder.DropColumn(
                name: "parent_signal_id",
                schema: "beacon",
                table: "mcp_query_signals");
        }
    }
}
