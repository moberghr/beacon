using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Beacon.Tests.Common;

namespace Beacon.Tests.Integration;

/// <summary>
/// Query-translation tests for Part A golden-exemplar generation grounding. Verifies the two new LINQ
/// queries — the indexing scan (active golden cases for a data source) and the retrieval projection
/// (nearest golden cases by owner id) used by <c>KnowledgeGraphService.BuildGoldenExemplarBlockAsync</c> —
/// translate to valid PostgreSQL via ToQueryString() (NpgsqlTestContext, no real DB; §4.4/§4.7).
/// </summary>
[TestFixture]
public class GoldenExemplarRetrievalTests : QueryTranslationTestBase
{
    /// <summary>
    /// The indexing job scans ACTIVE golden cases for a data source and embeds their masked questions.
    /// Guards the (DataSourceId, IsActive) scan + minimal projection translates.
    /// </summary>
    [Test]
    public void GoldenExemplarIndexingScan_ActiveByDataSource_Translates()
    {
        const int dataSourceId = 7;

        AssertQueryTranslates(ctx => ctx.McpEvalCases
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.IsActive)
            .Select(x => new
            {
                x.Id,
                x.Question
            }));

        var sql = Context.McpEvalCases
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.IsActive)
            .ToQueryString();

        Assert.That(sql, Does.Contain("mcp_eval_cases"), "expected the query to reference the mcp_eval_cases table");
        Assert.That(sql, Does.Contain("is_active"), "expected the IsActive filter to translate");
    }

    /// <summary>
    /// Retrieval loads the hit golden cases by owner id (the NN hit ids) scoped to the data source and
    /// active only, re-projected in similarity order. Guards the Contains-over-a-local-list filter plus the
    /// question + gold-SQL projection survive Npgsql translation.
    /// </summary>
    [Test]
    public void GoldenExemplarRetrieval_ByOwnerIds_Translates()
    {
        const int dataSourceId = 7;
        var ownerIds = new List<int> { 10, 20, 30 };

        AssertQueryTranslates(ctx => ctx.McpEvalCases
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.IsActive)
            .Where(x => ownerIds.Contains(x.Id))
            .Select(x => new
            {
                x.Id,
                x.Question,
                x.GoldSql
            }));

        var sql = Context.McpEvalCases
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.IsActive)
            .Where(x => ownerIds.Contains(x.Id))
            .ToQueryString();

        Assert.That(sql, Does.Contain("mcp_eval_cases"), "expected the query to reference the mcp_eval_cases table");
        Assert.That(sql, Does.Contain("gold_sql"), "expected the gold SQL column to be reachable in translation");
    }
}
