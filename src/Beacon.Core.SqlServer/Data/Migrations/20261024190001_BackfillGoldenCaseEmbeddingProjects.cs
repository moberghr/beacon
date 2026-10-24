using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Beacon.Core.SqlServer.Data.Migrations
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
                UPDATE beacon.McpEmbeddings
                SET ProjectId = (
                    SELECT c.ProjectId
                    FROM beacon.McpEvalCases AS c
                    WHERE c.Id = McpEmbeddings.OwnerId
                      AND c.DataSourceId = McpEmbeddings.DataSourceId)
                WHERE OwnerType = 5
                  AND ProjectId IS NULL
                  AND EXISTS (
                    SELECT 1
                    FROM beacon.McpEvalCases AS c
                    WHERE c.Id = McpEmbeddings.OwnerId
                      AND c.DataSourceId = McpEmbeddings.DataSourceId);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // No-op: which vectors had no project before Up() ran is not recorded, and the re-index stamps them anyway.
        }
    }
}
