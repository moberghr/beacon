using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;
using Beacon.Core.Handlers.McpAudit;
using Beacon.Tests.Common;

namespace Beacon.Tests.Integration;

/// <summary>
/// The admin audit export's filter and projection (<c>GetMcpAuditLogsHandler.Filtered</c> / <c>.Project</c>), the
/// statements of each call included, translate to valid PostgreSQL SQL, and the projection to SQL Server SQL, via
/// <c>ToQueryString()</c> (§4.3-§4.5); no database is opened.
/// </summary>
[TestFixture]
public class McpAuditQueryTranslationTests : QueryTranslationTestBase
{
    private static readonly GetMcpAuditLogsQuery BaseQuery = new(
        From: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        To: new DateTime(2026, 1, 31, 0, 0, 0, DateTimeKind.Utc),
        ProjectId: null,
        Tool: null,
        CallerHash: null,
        UserId: null,
        Page: 1,
        PageSize: 100,
        RequestedByUserId: 1);

    [Test]
    public void DateRangeOnly_Translates()
    {
        AssertQueryTranslates(context => GetMcpAuditLogsHandler.Filtered(context.McpAuditLogs, BaseQuery));
    }

    [Test]
    public void AllOptionalFilters_Translate()
    {
        var query = BaseQuery with { ProjectId = 1, Tool = "project_query", CallerHash = "abc", UserId = 2 };

        AssertQueryTranslates(context => GetMcpAuditLogsHandler.Filtered(context.McpAuditLogs, query));
    }

    [Test]
    public void ProjectionWithStatements_Translates()
    {
        var sql = GetMcpAuditLogsHandler
            .Project(GetMcpAuditLogsHandler.Filtered(Context.McpAuditLogs, BaseQuery))
            .Skip(0)
            .Take(100)
            .ToQueryString();

        sql.Should().Contain("content_hash").And.Contain("beacon.mcp_audit_statements")
            .And.Contain("sql_hash").And.Contain("error_class").And.Contain("duration_ms");
    }

    [Test]
    public void ProjectionWithStatements_OnSqlServer_Translates()
    {
        using var context = SqlServerTestContext.Create();

        var sql = GetMcpAuditLogsHandler
            .Project(GetMcpAuditLogsHandler.Filtered(context.McpAuditLogs, BaseQuery))
            .Skip(0)
            .Take(100)
            .ToQueryString();

        sql.Should().Contain("[beacon].[McpAuditStatements]").And.Contain("[ContentHash]")
            .And.Contain("[SqlHash]").And.Contain("[ErrorClass]").And.Contain("[DurationMs]").And.Contain("[Purpose]");
    }

    [Test]
    public void ProjectionWithPaging_Translates()
    {
        AssertQueryTranslates(context => GetMcpAuditLogsHandler
            .Project(GetMcpAuditLogsHandler.Filtered(context.McpAuditLogs, BaseQuery))
            .Skip(0)
            .Take(100));
    }
}
