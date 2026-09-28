using NUnit.Framework;
using Beacon.Core.Handlers.McpAudit;
using Beacon.Tests.Common;

namespace Beacon.Tests.Integration;

/// <summary>
/// SC10 — the admin audit export's filter and projection (<c>GetMcpAuditLogsHandler.Filtered</c> /
/// <c>.Project</c>) translate to valid PostgreSQL SQL via <c>ToQueryString()</c> (§4.3-§4.5); no database
/// is opened.
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
    public void ProjectionWithPaging_Translates()
    {
        AssertQueryTranslates(context => GetMcpAuditLogsHandler
            .Project(GetMcpAuditLogsHandler.Filtered(context.McpAuditLogs, BaseQuery))
            .Skip(0)
            .Take(100));
    }
}
