using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Beacon.Core.Data.Enums;
using Beacon.Tests.Common;

namespace Beacon.Tests.Integration;

/// <summary>
/// Query-translation tests for Part B correctness-feedback storage. Verifies the new <c>UserVerdict</c>
/// enum column filter and the feedback handler's golden-idempotency lookup translate to valid PostgreSQL
/// via ToQueryString() (NpgsqlTestContext, no real DB — §4.4/§4.7).
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
}
