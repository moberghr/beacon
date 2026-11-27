using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDescriptionItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "enable_doc_chunks_in_ask",
                schema: "beacon",
                table: "mcp_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "enable_doc_chunks_in_ask",
                schema: "beacon",
                table: "mcp_project_settings",
                type: "boolean",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "global_instruction",
                schema: "beacon",
                table: "mcp_project_settings",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "generated_by_model",
                schema: "beacon",
                table: "mcp_documentation_patches",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "mcp_description_items",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    schema_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    table_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    column_name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    source = table.Column<int>(type: "integer", nullable: false),
                    written_at = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    written_by_user_id = table.Column<int>(type: "integer", nullable: true),
                    generated_by_model = table.Column<string>(type: "text", nullable: true),
                    source_patch_id = table.Column<int>(type: "integer", nullable: true),
                    structure_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    stale_since = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_description_items", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_description_items_data_source_id_schema_name_table_name",
                schema: "beacon",
                table: "mcp_description_items",
                columns: new[] { "data_source_id", "schema_name", "table_name", "column_name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_description_items",
                schema: "beacon");

            migrationBuilder.DropColumn(
                name: "enable_doc_chunks_in_ask",
                schema: "beacon",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "enable_doc_chunks_in_ask",
                schema: "beacon",
                table: "mcp_project_settings");

            migrationBuilder.DropColumn(
                name: "global_instruction",
                schema: "beacon",
                table: "mcp_project_settings");

            migrationBuilder.DropColumn(
                name: "generated_by_model",
                schema: "beacon",
                table: "mcp_documentation_patches");
        }
    }
}
