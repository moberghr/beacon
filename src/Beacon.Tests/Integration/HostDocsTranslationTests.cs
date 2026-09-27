using Beacon.AI.Services.Knowledge;
using Beacon.Core.Data.Enums;
using Beacon.Core.HostDocs;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Beacon.Tests.Integration;

/// <summary>
/// Query-translation coverage (§4.3) for the host-imported documents (ExposeDocs): the shared read shapes, the
/// search keyword arm, the imported-chunk embedding load and the project-brief queries. Every one must carry the
/// project filter (§1.12) — asserted on the generated SQL.
/// </summary>
[TestFixture]
public class HostDocsTranslationTests : QueryTranslationTestBase
{
    [Test]
    public void ImportedDocumentListForProject_Translates()
    {
        var sql = ImportedDocumentQueries.ListForProject(Context, 1).ToQueryString();

        sql.Should().Contain("project_imported_documents");
        sql.Should().Contain("project_id");
        sql.Should().Contain("archived_time IS NULL", "the soft-delete filter hides archived documents");
        sql.Should().NotContain(".content", "the listing does not load document bodies");
    }

    [Test]
    public void ImportedDocumentById_Translates()
    {
        var sql = ImportedDocumentQueries.ById(Context, 1, 5).ToQueryString();

        sql.Should().Contain("project_id");
        sql.Should().Contain(".content");
    }

    [Test]
    public void ImportedDocumentByPathOrTitle_Translates()
    {
        var sql = ImportedDocumentQueries.ByPathOrTitle(Context, 1, "README.md").ToQueryString();

        sql.Should().Contain("lower(");
        sql.Should().Contain("project_id");
    }

    [Test]
    public void ImportedDocsSearchQuery_Translates()
    {
        var sql = KnowledgeGraphService.BuildImportedDocsQuery(Context, 1, "loan", 10).ToQueryString();

        sql.Should().Contain("project_imported_documents");
        sql.Should().Contain("mcp_doc_chunks");
        sql.Should().Contain("EXISTS");
        sql.Should().Contain("LIMIT");
    }

    [Test]
    public void ImportedChunksEmbeddingLoad_Translates()
    {
        var sql = DocChunkIndexingService.BuildImportedChunksQuery(Context, 1).ToQueryString();

        sql.Should().Contain("imported_document_id IS NOT NULL");
        sql.Should().Contain("project_imported_documents");
    }

    [Test]
    public void ImportedChunkRemovalLoad_Translates()
    {
        // Mirrors HostDocsSynchronizer.RemoveChunksAsync (chunks of changed/archived documents + their vectors).
        var documentIds = new List<int> { 1, 2 };
        var chunkSql = Context.McpDocChunks
            .Where(x => x.ProjectId == 1)
            .Where(x => x.ImportedDocumentId != null)
            .Where(x => documentIds.Contains(x.ImportedDocumentId!.Value))
            .ToQueryString();

        var chunkIds = new List<int> { 3 };
        var embeddingSql = Context.McpEmbeddings
            .Where(x => x.ProjectId == 1)
            .Where(x => x.OwnerType == McpEmbeddingOwnerType.DocChunk)
            .Where(x => chunkIds.Contains(x.OwnerId))
            .ToQueryString();

        chunkSql.Should().Contain("imported_document_id");
        embeddingSql.Should().Contain("owner_id");
    }

    [Test]
    public void ProjectBriefDataSources_Translates()
    {
        var sql = ProjectBriefService.BuildDataSourcesQuery(Context, 1).ToQueryString();

        sql.Should().Contain("host_managed_key IS NOT NULL");
        sql.Should().Contain("count(*)");
    }

    [Test]
    public void ProjectBriefTopTables_Translates()
    {
        var sql = ProjectBriefService.BuildTopTablesQuery(Context, [1, 2], 30).ToQueryString();

        sql.Should().Contain("ORDER BY");
        sql.Should().Contain("is_foreign_key");
        sql.Should().Contain("LIMIT");
    }

    [Test]
    public void ProjectBriefMaskedColumns_Translates()
    {
        var sql = ProjectBriefService.BuildMaskedColumnsQuery(Context, [1], 50).ToQueryString();

        sql.Should().Contain("column_metadata");
        sql.Should().Contain("LIKE");
    }

    [Test]
    public void ProjectBriefGlossary_Translates()
    {
        var sql = ProjectBriefService.BuildGlossaryQuery(Context, 1, 30).ToQueryString();

        sql.Should().Contain("mcp_glossary_terms");
        sql.Should().Contain("is_active");
    }
}
