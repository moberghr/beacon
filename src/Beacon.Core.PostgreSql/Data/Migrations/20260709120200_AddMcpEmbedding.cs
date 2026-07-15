using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddMcpEmbedding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "mcp_embeddings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    data_source_id = table.Column<int>(type: "integer", nullable: false),
                    owner_type = table.Column<int>(type: "integer", nullable: false),
                    owner_id = table.Column<int>(type: "integer", nullable: false),
                    embedding_bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    model = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    dimensions = table.Column<int>(type: "integer", nullable: false),
                    embedding_version = table.Column<int>(type: "integer", nullable: false),
                    created_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_mcp_embeddings", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ix_mcp_embeddings_data_source_id",
                table: "mcp_embeddings",
                column: "data_source_id");

            migrationBuilder.CreateIndex(
                name: "ix_mcp_embeddings_data_source_id_owner_type_owner_id",
                table: "mcp_embeddings",
                columns: new[] { "data_source_id", "owner_type", "owner_id" },
                unique: true);

            // DB-managed pgvector column + HNSW (cosine) index. These are deliberately invisible
            // to the EF model (Beacon.Core stays provider-neutral); the indexing job writes the
            // vector alongside embedding_bytes and PG similarity search uses the <=> operator.
            // Tables are created unqualified and resolve to the configured schema via the
            // connection's search_path (matching this repo's PG migration convention).
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector;");
            migrationBuilder.Sql("ALTER TABLE mcp_embeddings ADD COLUMN embedding vector(384);");
            migrationBuilder.Sql("CREATE INDEX ix_mcp_embeddings_embedding_hnsw ON mcp_embeddings USING hnsw (embedding vector_cosine_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the table cascades the DB-managed embedding column and its HNSW index.
            // The vector extension is left installed (a shared object other schemas may rely on).
            migrationBuilder.DropTable(
                name: "mcp_embeddings");
        }
    }
}
