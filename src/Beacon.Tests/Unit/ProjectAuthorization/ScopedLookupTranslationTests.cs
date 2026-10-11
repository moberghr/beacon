using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Handlers.DataQuality.GetDataContractDetail;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Handlers.Dashboards.GetDashboards;
using Beacon.Core.Handlers.QueryVersions;
using Beacon.Core.Models.Dashboards;
using Beacon.Core.Services;
using Beacon.Core.Services.Metadata;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.Notifications;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// The lookups that confine data-quality rules to their data source, keep curated descriptions, name people by label and
/// find the projects sharing a data source translate — on Npgsql, and the shapes new to project access on SQL Server too —
/// through the production code: the SQL is captured offline, nothing runs (§4.7).
/// </summary>
[TestFixture]
public class ScopedLookupTranslationTests
{
    [Test]
    public async Task DataQualityRuleCatalog_Translates()
    {
        var capture = new SqlCapture();
        await using var context = await capture.Factory().CreateDbContextAsync();

        var act = () => DataQualityRuleCatalog.LoadAsync(
            context,
            7,
            "sales",
            "orders",
            ["{\"referenceSchema\":\"sales\",\"referenceTable\":\"customers\",\"referenceColumn\":\"id\"}"],
            CancellationToken.None);

        await act.Should().ThrowAsync<SqlCapturedException>();
        var sql = capture.Commands.Should().ContainSingle().Subject;
        sql.Should().Contain("FROM beacon.database_metadata").And.Contain("data_source_id = @dataSourceId").And.Contain("schema_name");
        capture.CommandParameters[0]["@dataSourceId"].Should().Be(7);
    }

    [Test]
    public async Task DataQualityRuleCatalog_SqlServer_Translates()
    {
        var database = new CommandCapture();
        await using var context = await SqlServerFactory(database).CreateDbContextAsync();

        await DataQualityRuleCatalog.LoadAsync(
            context,
            7,
            "sales",
            "orders",
            ["{\"referenceSchema\":\"sales\",\"referenceTable\":\"customers\",\"referenceColumn\":\"id\"}"],
            CancellationToken.None);

        var sql = database.Commands.Should().ContainSingle().Subject.Text;
        sql.Should().Contain("FROM [beacon].[DatabaseMetadata]").And.Contain("[DataSourceId] = @");
        sql.Should().Contain("LOWER([d].[SchemaName])").And.Contain("LOWER([d].[TableName])");
    }

    [Test]
    public async Task PeopleLabel_Translates_OnBothProviders()
    {
        var postgres = new SqlCapture().ThenNoRows();
        var sqlServer = new CommandCapture();

        var onPostgres = () => new GetDataContractDetailHandler(postgres.Factory()).Handle(new GetDataContractDetailQuery(5), CancellationToken.None);
        var onSqlServer = () => new GetDataContractDetailHandler(SqlServerFactory(sqlServer)).Handle(new GetDataContractDetailQuery(5), CancellationToken.None);

        await onPostgres.Should().ThrowAsync<BeaconException>("no row was answered");
        await onSqlServer.Should().ThrowAsync<BeaconException>("no row was answered");
        postgres.Commands.Should().ContainSingle().Which.Should().MatchRegex(
            @"WHEN u\d*\.display_name IS NULL OR btrim\(u\d*\.display_name, E' \\t\\n\\r'\) = '' THEN 'User ' \|\| u\d*\.id::text\s+ELSE u\d*\.display_name");
        sqlServer.Commands.Should().ContainSingle().Which.Text.Should().MatchRegex(
            @"WHEN \[u\d*\]\.\[DisplayName\] IS NULL OR \[u\d*\]\.\[DisplayName\] = N'' THEN N'User ' \+ CONVERT\(varchar\(11\), \[u\d*\]\.\[Id\]\)",
            "SQL Server compares strings ignoring trailing blanks, so a blank display name equals ''");
    }

    [TestCase(null, "User 3")]
    [TestCase("", "User 3")]
    [TestCase("   ", "User 3")]
    [TestCase("Ada Lovelace", "Ada Lovelace")]
    public void PeopleLabel_InSqlAndInMemory_IsOneRule(string? displayName, string expected)
    {
        var inSql = UserLabel.OfUser.Compile()(new BeaconUser { Id = 3, ExternalId = "ext-ada", UserName = "ada", DisplayName = displayName });

        inSql.Should().Be(expected);
        UserLabel.Of(3, displayName).Should().Be(expected);
    }

    [Test]
    public async Task SharedSourceSettings_ProjectsSharingTheSources_Translate_OnBothProviders()
    {
        var settings = SettingsProviderMock.Create();
        var postgres = new SqlCapture().ThenRows([2]);
        var sqlServer = new CommandCapture { Rows = [[2]], Columns = ["id"] };

        await new SharedSourceSettings(postgres.Factory(), settings.Object).GetEffectiveSettingsAsync(1, [10, 11], CancellationToken.None);
        await new SharedSourceSettings(SqlServerFactory(sqlServer), settings.Object).GetEffectiveSettingsAsync(1, [10, 11], CancellationToken.None);

        var pg = postgres.Commands.Should().ContainSingle().Subject;
        pg.Should().Contain("p.archived_time IS NULL").And.Contain("FROM beacon.project_data_sources AS p0").And.Contain("= ANY (@dataSourceIds)");
        var ss = sqlServer.Commands.Should().ContainSingle().Subject.Text;
        ss.Should().Contain("[p].[ArchivedTime] IS NULL").And.Contain("FROM [beacon].[ProjectDataSources]").And.MatchRegex(@"\[DataSourceId\] IN \(@dataSourceIds\d*, @dataSourceIds\d*\)");
        pg.Should().NotContain("FALSE", "an explicit archived filter next to the soft-delete filter would collapse the read");
        ss.Should().NotContain("0 = 1");
        settings.Verify(x => x.GetEffectiveSettingsAsync(2, It.IsAny<CancellationToken>()), Times.Exactly(2), "the sharing project's guardrails tighten the asking project's");
    }

    [Test]
    public async Task SharedSourceGuardrails_Links_Translate_OnBothProviders()
    {
        var settings = SettingsProviderMock.Create();
        var postgres = new SqlCapture().ThenNoRows();
        var sqlServer = new CommandCapture { Columns = ["c0", "c1", "c2", "c3"] };
        await using var postgresContext = await postgres.Factory().CreateDbContextAsync();
        await using var sqlServerContext = await SqlServerFactory(sqlServer).CreateDbContextAsync();

        var onPostgres = await SharedSourceGuardrails.ReadAsync(postgresContext, settings.Object, 1, CancellationToken.None);
        var onSqlServer = await SharedSourceGuardrails.ReadAsync(sqlServerContext, settings.Object, 1, CancellationToken.None);

        onPostgres.Should().BeEmpty();
        onSqlServer.Should().BeEmpty();
        postgres.Commands.Should().ContainSingle().Which.Should().Contain("p.archived_time IS NULL").And.Contain("FROM beacon.project_data_sources").And.NotContain("FALSE");
        sqlServer.Commands.Should().ContainSingle().Which.Text.Should().Contain("[p].[ArchivedTime] IS NULL").And.Contain("[beacon].[ProjectDataSources]").And.NotContain("0 = 1");
    }

    [Test]
    public async Task CuratedDescriptions_Translates()
    {
        var capture = new SqlCapture();
        await using var context = await capture.Factory().CreateDbContextAsync();

        var act = () => CuratedDescriptions.LoadAsync(context, 7, [], CancellationToken.None);

        await act.Should().ThrowAsync<SqlCapturedException>();
        capture.Commands.Should().ContainSingle().Which.Should().Contain("FROM beacon.mcp_documentation_patches").And.Contain("data_source_id");
    }

    [Test]
    public async Task QueryVersionAuthorLabel_Translates()
    {
        var capture = new SqlCapture().AnsweringCountsWithOne();
        var service = new QueryVersionService(capture.Factory(), Mock.Of<IBeaconUserContext>(), NullLogger<QueryVersionService>.Instance);

        await service.GetVersionsAsync(1, new GetQueryVersionsQuery { QueryId = 1 }, ProjectSet.All, CancellationToken.None);

        capture.Commands.Should().Contain(x => x.Contains("'User ' || u.id::text") && x.Contains("FROM beacon.query_versions"));
        capture.Commands.Should().NotContain(x => x.Contains("user_name"), "an author is never named by user name");
    }

    [Test]
    public async Task DashboardOwnerLabel_Translates()
    {
        var capture = new SqlCapture().AnsweringCountsWithOne();
        var userContext = new Mock<IBeaconUserContext>();
        userContext.SetupGet(x => x.UserId).Returns("ext-ada");
        var handler = new GetDashboardsHandler(capture.Factory(), userContext.Object);

        await handler.Handle(new GetDashboardsQuery(new GetDashboardsRequest()), CancellationToken.None);

        capture.Commands.Should().Contain(x => x.Contains("'User '") && x.Contains("FROM beacon.dashboards"));
        capture.Commands.Should().NotContain(x => x.Contains("created_by_user_name"), "the stored user name of the owner is not returned");
    }

    private static IDbContextFactory<BeaconContext> SqlServerFactory(CommandCapture database)
    {
        var options = new DbContextOptionsBuilder<SqlServerTestContext>()
            .UseSqlServer("Server=localhost;Database=unused;Trusted_Connection=True;TrustServerCertificate=True")
            .AddInterceptors(database)
            .Options;
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new SqlServerTestContext(options));

        return factory.Object;
    }
}
