using Beacon.Core.Handlers.Queries;
using Beacon.Core.SavedQueries;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Beacon.Tests.Integration;

/// <summary>Query-translation coverage (§4.3) for the saved-query tool lookups.</summary>
[TestFixture]
public class SavedQueryToolsTranslationTests : QueryTranslationTestBase
{
    [Test]
    public void SavedQueryEnabledTools_Translates()
    {
        var sql = SavedQueryToolSource.BuildEnabledToolsQuery(Context).ToQueryString();

        sql.Should().Contain("mcp_tool_enabled");
        sql.Should().Contain("query_approval_requests");
        sql.Should().Contain("EXISTS", "the approval check is a correlated subquery");
        sql.Should().Contain("archived_time IS NULL", "an archived query is never a tool");
    }

    [Test]
    public void SavedQueryProjectMembership_Translates()
    {
        var sql = SavedQueryToolSource.BuildMembershipQuery(Context, [1, 2], [10, 11]).ToQueryString();

        sql.Should().Contain("project_data_sources");
        sql.Should().Contain("project_id");
        sql.Should().Contain("archived_time IS NULL");
    }

    [Test]
    public void SavedQueryRunnableVersion_Translates()
    {
        var sql = SavedQueryRunnableVersion.ForQuery(Context, 5).ToQueryString();

        sql.Should().Contain("query_versions");
        sql.Should().Contain("steps_json");
        sql.Should().Contain("query_approval_requests");
    }

    [Test]
    public void SavedQueryToolNameTaken_Translates()
    {
        var sql = SetQueryMcpToolHandler.BuildNameTakenQuery(Context, "loan_book", 5).ToQueryString();

        sql.Should().Contain("mcp_tool_name = ");
        sql.Should().Contain("archived_time IS NULL", "an archived query frees its tool name");
    }
}
