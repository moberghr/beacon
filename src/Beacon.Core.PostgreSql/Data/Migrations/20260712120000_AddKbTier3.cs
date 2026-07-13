using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddKbTier3 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "project_id",
                table: "mcp_embeddings",
                type: "integer",
                nullable: true);

            // Project-scoped doc-chunk / glossary embeddings leave data_source_id null (symmetric with the
            // nullable project_id) instead of the old magic-sentinel 0.
            migrationBuilder.AlterColumn<int>(
                name: "data_source_id",
                table: "mcp_embeddings",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: false);

            migrationBuilder.AddColumn<bool>(
                name: "enable_contextual_retrieval",
                table: "mcp_settings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "doc_chunk_window_sentences",
                table: "mcp_settings",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<int>(
                name: "doc_chunk_overlap_sentences",
                table: "mcp_settings",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "glossary_top_k",
                table: "mcp_settings",
                type: "integer",
                nullable: false,
                defaultValue: 5);

            migrationBuilder.AddColumn<int>(
                name: "doc_chunk_top_k",
                table: "mcp_settings",
                type: "integer",
                nullable: false,
                defaultValue: 6);

            migrationBuilder.CreateTable(
                name: "mcp_doc_chunks",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    source_section_id = table.Column<int>(type: "integer", nullable: false),
                    chunk_text = table.Column<string>(type: "text", nullable: false),
                    contextual_blurb = table.Column<string>(type: "text", nullable: true),
                    sort_order = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_doc_chunks", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "mcp_glossary_terms",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    data_source_id = table.Column<int>(type: "integer", nullable: true),
                    term = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    synonyms = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    definition = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    target_schema = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    target_table = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    target_column = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    metric_expression = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_glossary_terms", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_embeddings_project_id_owner_type",
                table: "mcp_embeddings",
                columns: new[] { "project_id", "owner_type" });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_doc_chunks_project_id",
                table: "mcp_doc_chunks",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_doc_chunks_project_id_source_section_id",
                table: "mcp_doc_chunks",
                columns: new[] { "project_id", "source_section_id" });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_glossary_terms_project_id",
                table: "mcp_glossary_terms",
                column: "project_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_glossary_terms_project_id_is_active",
                table: "mcp_glossary_terms",
                columns: new[] { "project_id", "is_active" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "mcp_doc_chunks");

            migrationBuilder.DropTable(
                name: "mcp_glossary_terms");

            migrationBuilder.DropIndex(
                name: "ix_mcp_embeddings_project_id_owner_type",
                table: "mcp_embeddings");

            migrationBuilder.DropColumn(
                name: "project_id",
                table: "mcp_embeddings");

            migrationBuilder.AlterColumn<int>(
                name: "data_source_id",
                table: "mcp_embeddings",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.DropColumn(
                name: "enable_contextual_retrieval",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "doc_chunk_window_sentences",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "doc_chunk_overlap_sentences",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "glossary_top_k",
                table: "mcp_settings");

            migrationBuilder.DropColumn(
                name: "doc_chunk_top_k",
                table: "mcp_settings");
        }
    }
}
