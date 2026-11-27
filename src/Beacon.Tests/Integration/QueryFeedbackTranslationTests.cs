using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Tests.Common;

namespace Beacon.Tests.Integration;

/// <summary>
/// Query-translation tests for Part B correctness-feedback storage. Verifies the new <c>UserVerdict</c>
/// enum column filter and the feedback handler's golden-idempotency lookup translate to valid PostgreSQL
/// via ToQueryString() (NpgsqlTestContext, no real DB — §4.4/§4.7). The signal provenance reads (a signal's context items,
/// the parent ask lookup) are checked on both providers (NpgsqlTestContext and SqlServerTestContext).
/// </summary>
[TestFixture]
public class QueryFeedbackTranslationTests : QueryTranslationTestBase
{
    /// <summary>
    /// The learning aggregator excludes human-verified-incorrect signals; guard that the new enum column
    /// maps and filters correctly under Npgsql (enum → integer column).
    /// </summary>
    [Test]
    public void QuerySignals_ByUserVerdict_Translates()
    {
        AssertQueryTranslates(ctx => ctx.McpQuerySignals
            .Where(x => x.UserVerdict != McpUserVerdict.Incorrect)
            .Select(x => new
            {
                x.Id,
                x.UserVerdict,
                x.GeneratedSql,
                x.TablesUsed
            }));

        var sql = Context.McpQuerySignals
            .Where(x => x.UserVerdict != McpUserVerdict.Incorrect)
            .ToQueryString();

        Assert.That(sql, Does.Contain("mcp_query_signals"), "expected the query to reference the mcp_query_signals table");
        Assert.That(sql, Does.Contain("user_verdict"), "expected the new UserVerdict column filter to translate");
    }

    /// <summary>
    /// The feedback handler's idempotency guard: a Correct verdict only promotes when no golden case
    /// already references this signal. Guard the (SourceSignalId) existence lookup translates.
    /// </summary>
    [Test]
    public void EvalCases_BySourceSignal_Translates()
    {
        const int signalId = 812;

        AssertQueryTranslates(ctx => ctx.McpEvalCases
            .Where(x => x.SourceSignalId == signalId)
            .Select(x => new { x.Id, x.SourceSignalId }));

        var sql = Context.McpEvalCases
            .Where(x => x.SourceSignalId == signalId)
            .ToQueryString();

        Assert.That(sql, Does.Contain("mcp_eval_cases"), "expected the query to reference the mcp_eval_cases table");
        Assert.That(sql, Does.Contain("source_signal_id"), "expected the SourceSignalId idempotency filter to translate");
    }

    /// <summary>
    /// The knowledge items a signal's prompt carried, in rendered order: filtered on the signal's key and ordered by rank.
    /// </summary>
    [TestCase(false, "mcp_signal_context_items", ".signal_id = @", @"ORDER BY \w+\.rank\s*$")]
    [TestCase(true, "[McpSignalContextItems]", ".[SignalId] = @", @"ORDER BY \[\w+\]\.\[Rank\]\s*$")]
    public void SignalContextItems_ByRank_Translates(bool sqlServer, string table, string signalFilter, string rankOrder)
    {
        using BeaconContext context = sqlServer ? SqlServerTestContext.Create() : NpgsqlTestContext.Create();
        var signalId = 812;

        var sql = context.McpSignalContextItems
            .Where(x => x.SignalId == signalId)
            .OrderBy(x => x.Rank)
            .Select(x =>
                new
                {
                    x.Kind,
                    x.ItemId,
                    x.Rank
                })
            .ToQueryString();

        Assert.That(sql, Does.Contain(table), "expected the query to read the context items table");
        Assert.That(sql, Does.Contain(signalFilter), "expected the items to be filtered on their signal");
        Assert.That(sql, Does.Match(rankOrder), "expected the items in rank order, by rank alone");
    }

    /// <summary>
    /// The parent lookup for a <c>query</c> or <c>dry_run</c> signal from a mapped caller: the newest <c>ask</c> signal of the
    /// same project, data source and caller hash since the cutoff, reading only its id and candidate SQL hash.
    /// </summary>
    [TestCase(false, "mcp_query_signals", "tool = 'ask'", ".caller_hash = @", ".created_time >= @", ".id DESC", "LIMIT ", "candidate_sql_hash")]
    [TestCase(true, "[McpQuerySignals]", "[Tool] = N'ask'", ".[CallerHash] = @", ".[CreatedTime] >= @", ".[Id] DESC", "TOP(", "[CandidateSqlHash]")]
    public void ParentAskSignal_ByCallerHash_Translates(
        bool sqlServer, string table, string tool, string caller, string cutoffFilter, string newestFirst, string single, string candidateHash)
    {
        using BeaconContext context = sqlServer ? SqlServerTestContext.Create() : NpgsqlTestContext.Create();
        var projectId = 7;
        var dataSourceId = 11;
        var callerHash = "c0ffee";
        var cutoff = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

        var sql = context.McpQuerySignals
            .Where(x => x.Tool == "ask")
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.CallerHash == callerHash)
            .Where(x => x.CreatedTime >= cutoff)
            .OrderByDescending(x => x.Id)
            .Select(x =>
                new
                {
                    x.Id,
                    x.CandidateSqlHash
                })
            .Take(1)
            .ToQueryString();

        Assert.That(sql, Does.Contain(table), "expected the query to read the signals table");
        Assert.That(sql, Does.Contain(tool), "expected only ask signals to be parents");
        Assert.That(sql, Does.Contain(caller), "expected the same caller to be matched by caller hash");
        Assert.That(sql, Does.Contain(cutoffFilter), "expected the lookup window to translate");
        Assert.That(sql, Does.Contain(newestFirst), "expected the newest ask signal first");
        Assert.That(sql, Does.Contain(single), "expected a single row to be read");
        Assert.That(sql, Does.Contain(candidateHash), "expected the parent's candidate SQL hash to be read");
    }

    /// <summary>
    /// The same parent lookup for an API key or session caller, matched on the user id instead of a caller hash.
    /// </summary>
    [TestCase(false, ".user_id = @", ".project_id = @", ".data_source_id = @", "caller_hash", ".id DESC")]
    [TestCase(true, ".[UserId] = @", ".[ProjectId] = @", ".[DataSourceId] = @", "[CallerHash]", ".[Id] DESC")]
    public void ParentAskSignal_ByUser_Translates(
        bool sqlServer, string user, string project, string dataSource, string callerHash, string newestFirst)
    {
        using BeaconContext context = sqlServer ? SqlServerTestContext.Create() : NpgsqlTestContext.Create();
        var projectId = 7;
        var dataSourceId = 11;
        var userId = 42;
        var cutoff = new DateTime(2026, 11, 27, 12, 0, 0, DateTimeKind.Utc);

        var sql = context.McpQuerySignals
            .Where(x => x.Tool == "ask")
            .Where(x => x.ProjectId == projectId)
            .Where(x => x.DataSourceId == dataSourceId)
            .Where(x => x.UserId == userId)
            .Where(x => x.CreatedTime >= cutoff)
            .OrderByDescending(x => x.Id)
            .Select(x =>
                new
                {
                    x.Id,
                    x.CandidateSqlHash
                })
            .Take(1)
            .ToQueryString();

        Assert.That(sql, Does.Contain(user), "expected the same caller to be matched by user id");
        Assert.That(sql, Does.Contain(project), "expected the same project to be required");
        Assert.That(sql, Does.Contain(dataSource), "expected the same data source to be required");
        Assert.That(sql, Does.Not.Contain(callerHash), "expected no caller hash filter for a user-matched caller");
        Assert.That(sql, Does.Contain(newestFirst), "expected the newest ask signal first");
    }
}
