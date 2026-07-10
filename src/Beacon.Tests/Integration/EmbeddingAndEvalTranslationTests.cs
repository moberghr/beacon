using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Beacon.Core.Data.Enums;
using Beacon.Tests.Common;

namespace Beacon.Tests.Integration;

/// <summary>
/// Query-translation tests for the KB / self-learning embedding + eval storage (Batch B3 onward).
/// Verifies handler-style LINQ over the new tables translates to valid PostgreSQL SQL via
/// ToQueryString() (NpgsqlTestContext, no real DB). Later batches (B5/B6/B8/B9) extend this file.
/// </summary>
[TestFixture]
public class EmbeddingAndEvalTranslationTests : QueryTranslationTestBase
{
    // ─── Embedding storage (B3) ──────────────────────────────────────

    /// <summary>
    /// Mirrors the vector-store lookup scope used by the indexing job and retrieval paths:
    /// embeddings are scoped by DataSourceId and filtered by OwnerType (table/column/exemplar).
    /// Guards that the enum filter + byte[] column projection survive Npgsql translation.
    /// </summary>
    [Test]
    public void McpEmbeddings_ByDataSourceAndOwnerType_Translates()
    {
        const int dataSourceId = 7;

        AssertQueryTranslates(ctx => ctx.McpEmbeddings
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.OwnerType == McpEmbeddingOwnerType.Exemplar)
            .OrderBy(x => x.OwnerId)
            .Select(x => new
            {
                x.Id,
                x.OwnerId,
                x.EmbeddingBytes,
                x.Model,
                x.Dimensions,
                x.EmbeddingVersion
            }));

        var sql = Context.McpEmbeddings
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.OwnerType == McpEmbeddingOwnerType.Exemplar)
            .ToQueryString();

        Assert.That(sql, Does.Contain("mcp_embeddings"), "expected the query to reference the mcp_embeddings table");
        Assert.That(sql, Does.Contain("data_source_id"), "expected the DataSourceId scope filter to translate");
    }

    // ─── Hybrid retrieval lexical arm (B5) ───────────────────────────

    /// <summary>
    /// The lexical (sparse) arm of the hybrid <c>SearchAsync</c> is also the behaviour-preserving
    /// fallback used whenever the embedder is unavailable, semantic retrieval is off, no data source
    /// is scoped, or the dense arm faults. The dense arm's raw pgvector <c>&lt;=&gt;</c> query cannot be
    /// exercised via ToQueryString() (it's not EF-translated), so we guard the arm that always runs:
    /// the token-overlap table match must survive Npgsql translation (Any-over-a-local-list + ToLower).
    /// </summary>
    [Test]
    public void HybridSearch_LexicalTableArm_Translates()
    {
        const int dataSourceId = 3;
        var terms = new List<string> { "order", "customer" };

        AssertQueryTranslates(ctx => ctx.DatabaseMetadata
            .Where(m => m.DataSourceId == dataSourceId)
            .Where(m => terms.Any(t => m.TableName.ToLower().Contains(t)) ||
                        (m.TableDescription != null && terms.Any(t => m.TableDescription.ToLower().Contains(t))))
            .Select(m => new
            {
                m.DataSourceId,
                DataSourceName = m.DataSource.Name,
                m.SchemaName,
                m.TableName,
                m.TableDescription
            }));
    }

    /// <summary>Companion to the table arm: the column-level token-overlap match must also translate.</summary>
    [Test]
    public void HybridSearch_LexicalColumnArm_Translates()
    {
        const int dataSourceId = 3;
        var terms = new List<string> { "email", "status" };

        AssertQueryTranslates(ctx => ctx.ColumnMetadata
            .Where(c => c.DatabaseMetadata.DataSourceId == dataSourceId)
            .Where(c => terms.Any(t => c.ColumnName.ToLower().Contains(t)) ||
                        (c.Description != null && terms.Any(t => c.Description.ToLower().Contains(t))))
            .Select(c => new
            {
                c.DatabaseMetadata.DataSourceId,
                DataSourceName = c.DatabaseMetadata.DataSource.Name,
                c.DatabaseMetadata.SchemaName,
                c.DatabaseMetadata.TableName,
                c.ColumnName,
                c.Description
            }));
    }

    /// <summary>
    /// The SQL-Server / non-Npgsql dense arm loads candidate embeddings by data source + owner type and
    /// does cosine in memory. This is the EF query that feeds that path; guard it translates (the cosine
    /// itself is plain in-memory math, covered by <c>EmbeddingCodecTests</c>).
    /// </summary>
    [Test]
    public void HybridSearch_DenseCandidateLoad_Translates()
    {
        const int dataSourceId = 3;

        AssertQueryTranslates(ctx => ctx.McpEmbeddings
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.OwnerType == McpEmbeddingOwnerType.MetadataTable || x.OwnerType == McpEmbeddingOwnerType.MetadataColumn)
            .Select(x => new
            {
                x.OwnerType,
                x.OwnerId,
                x.EmbeddingBytes
            }));
    }

    // ─── Eval harness (B8) ───────────────────────────────────────────

    /// <summary>
    /// The eval run reads its per-case outcomes by joining <c>McpEvalResults</c> to their
    /// <c>McpEvalCases</c> on the plain int FK (<c>EvalCaseId</c>) and scoping by <c>EvalRunId</c>
    /// (the read path behind GetEvalResults). Guards that the manual join + enum failure-tag
    /// projection survive Npgsql translation and reference both eval tables.
    /// </summary>
    [Test]
    public void EvalResults_ByRunJoinedToCases_Translates()
    {
        const int evalRunId = 42;

        AssertQueryTranslates(ctx =>
            from r in ctx.McpEvalResults.Where(x => x.EvalRunId == evalRunId)
            join c in ctx.McpEvalCases on r.EvalCaseId equals c.Id
            orderby r.Id
            select new
            {
                r.Id,
                r.Passed,
                r.FailureTag,
                r.GeneratedSql,
                r.ResultRowCount,
                r.ExecutionTimeMs,
                c.Question,
                c.GoldSql
            });

        var sql = Context.McpEvalResults
            .Where(x => x.EvalRunId == evalRunId)
            .ToQueryString();

        Assert.That(sql, Does.Contain("mcp_eval_results"), "expected the query to reference the mcp_eval_results table");
        Assert.That(sql, Does.Contain("eval_run_id"), "expected the EvalRunId scope filter to translate");
    }

    /// <summary>
    /// Selecting the active golden set for a data source is the harness entry point (RunEval reads
    /// active cases scoped by data source). Guards the (DataSourceId, IsActive) filter translates.
    /// </summary>
    [Test]
    public void EvalCases_ActiveByDataSource_Translates()
    {
        const int dataSourceId = 7;

        AssertQueryTranslates(ctx => ctx.McpEvalCases
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.IsActive)
            .OrderBy(x => x.Id)
            .Select(x => new
            {
                x.Id,
                x.Question,
                x.GoldSql,
                x.GoldResultFingerprint,
                x.SourceSignalId
            }));

        var sql = Context.McpEvalCases
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.IsActive)
            .ToQueryString();

        Assert.That(sql, Does.Contain("mcp_eval_cases"), "expected the query to reference the mcp_eval_cases table");
        Assert.That(sql, Does.Contain("is_active"), "expected the IsActive filter to translate");
    }
}
