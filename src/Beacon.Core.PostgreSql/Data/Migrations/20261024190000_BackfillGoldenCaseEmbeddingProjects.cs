using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.PostgreSql.Data.Migrations
{
    /// <summary>
    /// Data only, no schema change. A golden-case vector indexed before vectors carried their project is stamped with its
    /// case's project, so retrieval filtered on the caller's project finds it without re-embedding. Only a vector without a
    /// project whose case is on the vector's data source is stamped. Owner type 5 is <c>McpEmbeddingOwnerType.GoldenCase</c>.
    /// </summary>
    public partial class BackfillGoldenCaseEmbeddingProjects : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                UPDATE beacon.mcp_embeddings
                SET project_id = (
                    SELECT c.project_id
                    FROM beacon.mcp_eval_cases AS c
                    WHERE c.id = mcp_embeddings.owner_id
                      AND c.data_source_id = mcp_embeddings.data_source_id)
                WHERE owner_type = 5
                  AND project_id IS NULL
                  AND EXISTS (
                    SELECT 1
                    FROM beacon.mcp_eval_cases AS c
                    WHERE c.id = mcp_embeddings.owner_id
                      AND c.data_source_id = mcp_embeddings.data_source_id);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: which vectors had no project before Up() ran is not recorded, and the re-index stamps them anyway.
        }
    }
}
