using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Beacon.Core.Data.Enums;
using Beacon.Tests.Common;

namespace Beacon.Tests.Integration;

/// <summary>
/// Query-translation coverage (§4.3) for the Tier-3 glossary (⑪) and doc-chunk (⑨) queries added in this
/// batch. Each mirrors a real query and is validated via <c>ToQueryString()</c> against the Npgsql provider
/// on a dummy connection (no DB hit — §4.7). Asserts non-empty SQL that references the expected table so a
/// provider-side translation break (bad filter, unsupported operator) fails here rather than at runtime.
/// </summary>
[TestFixture]
public class KbTier3TranslationTests : QueryTranslationTestBase
{
    [Test]
    public void GetGlossaryTermsQuery_FilteredByProjectAndActive_Translates()
    {
        // Mirrors GetGlossaryTermsHandler (active-only default) and the injection term-load in
        // KnowledgeGraphService.BuildGlossaryBlockAsync.
        var sql = Context.McpGlossaryTerms
            .Where(x => x.ProjectId == 1)
            .Where(x => x.IsActive)
            .OrderBy(x => x.Term)
            .Select(x =>
                new
                {
                    x.Id,
                    x.Term,
                    x.Definition,
                    x.TargetSchema,
                    x.TargetTable,
                    x.TargetColumn,
                    x.MetricExpression,
                    x.IsActive
                })
            .ToQueryString();

        sql.Should().NotBeNullOrEmpty();
        sql.ToLowerInvariant().Should().Contain("mcp_glossary_terms");
    }

    [Test]
    public void GetDocChunksQuery_FilteredByProjectAndSection_Translates()
    {
        // Mirrors DocChunkIndexingService's per-section chunk load and the retrieval seam
        // (GetRelevantDocChunksAsync) filtering by project + source section.
        var sql = Context.McpDocChunks
            .Where(x => x.ProjectId == 1)
            .Where(x => x.SourceSectionId == 2)
            .OrderBy(x => x.SortOrder)
            .Select(x =>
                new
                {
                    x.Id,
                    x.ChunkText,
                    x.ContextualBlurb,
                    x.SortOrder
                })
            .ToQueryString();

        sql.Should().NotBeNullOrEmpty();
        sql.ToLowerInvariant().Should().Contain("mcp_doc_chunks");
    }

    [Test]
    public void GetEmbeddingCandidatesQuery_ProjectScopedByOwnerType_Translates()
    {
        // Mirrors the project-scoped in-memory NN candidate query in
        // KnowledgeGraphService.GetNearestInMemoryAsync (glossary/doc-chunk retrieval): scope by project_id,
        // filter to the target owner type(s), project the bytes for in-process cosine.
        var ownerTypes = new[] { (int)McpEmbeddingOwnerType.GlossaryTerm };
        var ownerTypeFilter = ownerTypes
            .Select(x => (McpEmbeddingOwnerType)x)
            .ToList();

        var sql = Context.McpEmbeddings
            .Where(x => x.ProjectId == 1)
            .Where(x => ownerTypeFilter.Contains(x.OwnerType))
            .Select(x =>
                new
                {
                    x.OwnerType,
                    x.OwnerId,
                    x.EmbeddingBytes
                })
            .ToQueryString();

        sql.Should().NotBeNullOrEmpty();
        sql.ToLowerInvariant().Should().Contain("mcp_embeddings");
    }
}
