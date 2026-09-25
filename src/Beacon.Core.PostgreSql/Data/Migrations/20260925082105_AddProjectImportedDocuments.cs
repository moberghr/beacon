using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddProjectImportedDocuments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "imported_document_id",
                schema: "beacon",
                table: "mcp_doc_chunks",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "project_imported_documents",
                schema: "beacon",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    project_id = table.Column<int>(type: "integer", nullable: false),
                    source_key = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    path = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    content_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    content = table.Column<string>(type: "text", nullable: false),
                    frontmatter_json = table.Column<string>(type: "text", nullable: true),
                    imported_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    archived_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_project_imported_documents", x => x.id);
                    table.ForeignKey(
                        name: "fk_project_imported_documents_projects_project_id",
                        column: x => x.project_id,
                        principalSchema: "beacon",
                        principalTable: "projects",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_doc_chunks_imported_document_id",
                schema: "beacon",
                table: "mcp_doc_chunks",
                column: "imported_document_id");

            migrationBuilder.CreateIndex(
                name: "ix_project_imported_documents_project_id_source_key_path",
                schema: "beacon",
                table: "project_imported_documents",
                columns: new[] { "project_id", "source_key", "path" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "fk_mcp_doc_chunks_project_imported_documents_imported_document",
                schema: "beacon",
                table: "mcp_doc_chunks",
                column: "imported_document_id",
                principalSchema: "beacon",
                principalTable: "project_imported_documents",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "fk_mcp_doc_chunks_project_imported_documents_imported_document",
                schema: "beacon",
                table: "mcp_doc_chunks");

            migrationBuilder.DropTable(
                name: "project_imported_documents",
                schema: "beacon");

            migrationBuilder.DropIndex(
                name: "ix_mcp_doc_chunks_imported_document_id",
                schema: "beacon",
                table: "mcp_doc_chunks");

            migrationBuilder.DropColumn(
                name: "imported_document_id",
                schema: "beacon",
                table: "mcp_doc_chunks");
        }
    }
}
