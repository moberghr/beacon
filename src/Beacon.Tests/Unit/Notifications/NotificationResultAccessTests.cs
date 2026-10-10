using System.Net;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Api.Endpoints;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.Notifications;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Notifications;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.Notifications;

/// <summary>
/// The notifications endpoints return a run's stored result rows (detail) and its metadata (list) only within the
/// caller's projects: a project-restricted caller sees a run only when one of its projects holds every data source the
/// run recorded reading, and never a run recorded before data sources were kept. Signed-in users without a project
/// restriction read every run; anonymous and scoped callers without a restriction read none. A stored comment leaves
/// only when it is a recorded failure reason.
/// </summary>
[TestFixture]
public class NotificationResultAccessTests
{
    private const int ProjectOne = 1;
    private const int ProjectTwo = 2;
    private const int ProjectBoth = 3;
    private const int ProjectOneSource = 10;
    private const int ProjectTwoSource = 20;
    private const int SharedSource = 30;
    private const int UnassignedSource = 40;
    private const string StoredRows = "[{\"iban\":\"HR1210010051863000160\",\"customer\":\"Project B customer\"}]";
    private const string LegacyComment = "Recipient 3: connection to 10.0.0.5 refused (raw driver text)";
    private const string RecordedComment = "Recipient 3: " + NotificationFailureReasons.ConnectionFailed;

    private static readonly int[] RunIds = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    // --- the HTTP surface ---------------------------------------------------------------------------------------

    [Test]
    public async Task Detail_ProjectRestrictedKey_GetsNotFoundForAnotherProjectsRun()
    {
        await using var app = await StartAsync(ApiKey("[1]"));
        var client = app.GetTestClient();

        var projectResponse = await client.GetAsync("/beacon/api/projects/2/imported-documents");
        var response = await client.GetAsync("/beacon/api/notifications/2");

        projectResponse.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("HR1210010051863000160");
    }

    [Test]
    public async Task Detail_ProjectRestrictedKey_ReadsItsOwnProjectsRun()
    {
        await using var app = await StartAsync(ApiKey("[1]"));

        var response = await app.GetTestClient().GetAsync("/beacon/api/notifications/1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("entry").GetProperty("results").GetString().Should().Be(StoredRows);
    }

    [Test]
    public async Task List_ProjectRestrictedKey_SeesOnlyItsProjectsRuns_AndOnlyRecordedReasons()
    {
        await using var app = await StartAsync(ApiKey("[1]"));

        var response = await app.GetTestClient().GetAsync("/beacon/api/notifications?pageSize=50");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var items = json.RootElement.GetProperty("items").EnumerateArray().ToList();
        items.Select(x => x.GetProperty("id").GetInt32()).Should().BeEquivalentTo([1, 3, 7, 11]);
        items.Single(x => x.GetProperty("id").GetInt32() == 1).GetProperty("comment").GetString().Should().Be(NotificationFailureReasons.Generic);
        items.Single(x => x.GetProperty("id").GetInt32() == 11).GetProperty("comment").GetString().Should().Be(RecordedComment);
        body.Should().NotContain("10.0.0.5");
    }

    [Test]
    public async Task ScopedKeyWithoutAProjectRestriction_ReadsNoRun()
    {
        await using var app = await StartAsync(Principal(("auth_method", "api_key"), ("scope", "Read")));
        var client = app.GetTestClient();

        var detail = await client.GetAsync("/beacon/api/notifications/1");
        using var list = JsonDocument.Parse(await client.GetStringAsync("/beacon/api/notifications"));
        var project = await client.GetAsync("/beacon/api/projects/1/imported-documents");

        detail.StatusCode.Should().Be(HttpStatusCode.NotFound);
        list.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
        project.StatusCode.Should().Be(HttpStatusCode.Forbidden, "a scoped caller without projects is denied every project, and every run");
    }

    // --- who reads which run ------------------------------------------------------------------------------------

    [Test]
    public async Task Handler_SignedInUserWithoutProjectRestriction_ReadsEveryRun_IncludingOnesWithoutARecord()
    {
        var interactive = Principal((ClaimTypes.NameIdentifier, "ext-1"), (ClaimTypes.Role, "Viewer"));

        foreach (var id in RunIds)
        {
            var result = await DetailHandler(interactive).Handle(new GetNotificationDetailQuery(id), CancellationToken.None);

            result.Entry.Should().NotBeNull($"run {id} is readable by a caller without a project restriction");
            result.Entry!.Results.Should().Be(StoredRows);
        }
    }

    [TestCaseSource(nameof(CallersThatReadNoRun))]
    public async Task Handler_CallerThatReadsNoRun_GetsNoDetail_AndAnEmptyList(ClaimsPrincipal? caller)
    {
        var accessor = new HttpContextAccessor { HttpContext = caller == null ? null : new DefaultHttpContext { User = caller } };
        var service = CreateService(Factory());

        foreach (var id in RunIds)
        {
            var result = await new GetNotificationDetailHandler(service, accessor).Handle(new GetNotificationDetailQuery(id), CancellationToken.None);
            result.Entry.Should().BeNull($"run {id} is not readable");
        }

        var page = await new GetNotificationsHandler(service, accessor).Handle(new GetNotificationsQuery { PageSize = 50 }, CancellationToken.None);
        page.Items.Should().BeEmpty();
    }

    [TestCase("[1]", 1, true)]
    [TestCase("[1]", 2, false)]
    [TestCase("[1]", 3, true)]
    [TestCase("[2]", 3, true)]
    [TestCase("[1]", 4, false)]
    [TestCase("[1,2,3]", 5, false)]
    [TestCase("[1]", 6, false)]
    [TestCase("[1,2]", 6, false)]
    [TestCase("[3]", 6, true)]
    [TestCase("[2,3]", 6, true)]
    [TestCase("[1]", 7, true)]
    [TestCase("[2]", 7, false)]
    [TestCase("[1,2,3]", 8, false)]
    [TestCase("[1,2,3]", 9, false)]
    [TestCase("[1,2,3]", 10, false)]
    [TestCase("[]", 1, false)]
    [TestCase("", 1, false)]
    [TestCase("null", 1, false)]
    [TestCase("not-json", 1, false)]
    public async Task Handler_ProjectRestrictedCaller_ReadsARunOnlyWhenOneOfItsProjectsHoldsEverySourceTheRunRead(
        string allowedProjects, int runId, bool readable)
    {
        var caller = Principal(("auth_method", "mcp_caller"), ("scope", "Read"), ("allowed_projects", allowedProjects));
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = caller } };
        var service = CreateService(Factory());

        var detail = await new GetNotificationDetailHandler(service, accessor).Handle(new GetNotificationDetailQuery(runId), CancellationToken.None);
        var page = await new GetNotificationsHandler(service, accessor).Handle(new GetNotificationsQuery { PageSize = 50 }, CancellationToken.None);

        if (readable)
        {
            detail.Entry.Should().NotBeNull();
            detail.Entry!.Results.Should().Be(StoredRows);
            page.Items.Select(x => x.Id).Should().Contain(runId);
        }
        else
        {
            detail.Entry.Should().BeNull();
            page.Items.Select(x => x.Id).Should().NotContain(runId);
        }
    }

    [Test]
    public void Scope_OfEachKindOfCaller()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ext-1")]));

        StoredRunAccess.ScopeOf(null).Should().BeSameAs(StoredRunScope.None);
        StoredRunAccess.ScopeOf(anonymous).Should().BeSameAs(StoredRunScope.None);
        StoredRunAccess.ScopeOf(Principal(("auth_method", "api_key"), ("scope", "Read"))).Should().BeSameAs(StoredRunScope.None);
        StoredRunAccess.ScopeOf(Principal(("auth_method", "mcp_caller"), ("scope", "Read"))).Should().BeSameAs(StoredRunScope.None);
        StoredRunAccess.ScopeOf(Principal((ClaimTypes.NameIdentifier, "ext-1"))).Should().BeSameAs(StoredRunScope.Unrestricted);
        StoredRunAccess.ScopeOf(ApiKey("[2,1,2]")).AllowedProjectIds.Should().Equal(2, 1);
        StoredRunAccess.ScopeOf(ApiKey("")).AllowedProjectIds.Should().BeEmpty();
        StoredRunAccess.ScopeOf(Principal((ClaimTypes.NameIdentifier, "ext-1"), ("allowed_projects", "[1]")))
            .AllowedProjectIds.Should().Equal([1], "whoever carries the claim is restricted by it");
    }

    [TestCase("")]
    [TestCase("  ")]
    [TestCase("null")]
    [TestCase("[null]")]
    [TestCase("{}")]
    [TestCase("[1,\"a\"]")]
    [TestCase("not-json")]
    public void ProjectRestriction_BlankOrMalformedClaim_AllowsNoProject(string claim)
    {
        ProjectRestriction.Of(Principal(("allowed_projects", claim))).Should().NotBeNull().And.BeEmpty();
    }

    [Test]
    public void ProjectRestriction_NoClaim_IsNoRestriction_AndAWellFormedClaimIsItsProjects()
    {
        ProjectRestriction.Of(null).Should().BeNull();
        ProjectRestriction.Of(Principal(("auth_method", "api_key"))).Should().BeNull();
        ProjectRestriction.Of(Principal(("allowed_projects", "[4, 2]"))).Should().Equal(4, 2);
    }

    // --- stored comments ----------------------------------------------------------------------------------------

    [TestCaseSource(nameof(RecordedComments))]
    public void Comment_RecordedReason_IsShownAsRecorded(string comment)
    {
        NotificationFailureReasons.Displayable(comment).Should().Be(comment);
    }

    [TestCase(LegacyComment)]
    [TestCase("Recipient 3: Notification delivery failed: connection failed. (10.0.0.5)")]
    [TestCase("Notification delivery failed: the destination returned HTTP 9xx.")]
    [TestCase("Recipient 3:" + NotificationFailureReasons.Generic)]
    [TestCase(NotificationFailureReasons.Generic + " ")]
    [TestCase("Query execution failed: relation \"accounts\" does not exist")]
    [TestCase("")]
    public void Comment_AnythingElse_IsShownAsTheGenericReason(string comment)
    {
        NotificationFailureReasons.Displayable(comment).Should().Be(NotificationFailureReasons.Generic);
    }

    [Test]
    public void Comment_None_StaysNone()
    {
        NotificationFailureReasons.Displayable(null).Should().BeNull();
    }

    // --- translation ------------------------------------------------------------------------------------------------

    [Test]
    public async Task NotificationDetails_RestrictedRead_Translates()
    {
        var capture = new SqlCapture().ThenNoRows();

        var details = await CreateService(capture.Factory()).GetNotificationDetails(7, StoredRunScope.WithinProjects([ProjectOne, ProjectTwo]), CancellationToken.None);

        details.Should().BeNull();
        var sql = capture.Commands.Should().ContainSingle().Subject;
        RestrictedPredicateOf(sql);
        capture.CommandParameters[0].Values.OfType<IEnumerable<int>>().Should().ContainSingle()
            .Which.Should().Equal(ProjectOne, ProjectTwo);
        capture.CommandParameters[0].Values.Should().Contain(7);
    }

    [Test]
    public async Task NotificationList_RestrictedRead_Translates()
    {
        var capture = new SqlCapture().ThenScalar(0);

        await CreateService(capture.Factory()).GetQueryExecutionHistory(
            new GetQueryExecutionHistoryRequest { Scope = StoredRunScope.WithinProjects([ProjectOne]) },
            CancellationToken.None);

        var count = capture.Commands.Should().ContainSingle("an empty count skips the page query").Subject;
        RestrictedPredicateOf(count);
        capture.CommandParameters[0].Values.OfType<IEnumerable<int>>().Should().ContainSingle()
            .Which.Should().Equal(ProjectOne);
    }

    [Test]
    public async Task NotificationList_NoProjectAllowed_StillAppliesThePredicate_Translates()
    {
        var capture = new SqlCapture().ThenScalar(0);

        await CreateService(capture.Factory()).GetQueryExecutionHistory(
            new GetQueryExecutionHistoryRequest { Scope = StoredRunScope.None },
            CancellationToken.None);

        RestrictedPredicateOf(capture.Commands.Should().ContainSingle().Subject);
        capture.CommandParameters[0].Values.OfType<IEnumerable<int>>().Should().ContainSingle().Which.Should().BeEmpty();
    }

    [Test]
    public async Task NotificationList_Unrestricted_ReadsWithoutThePredicate_Translates()
    {
        var capture = new SqlCapture().ThenScalar(0);

        await CreateService(capture.Factory()).GetQueryExecutionHistory(
            new GetQueryExecutionHistoryRequest { Scope = StoredRunScope.Unrestricted },
            CancellationToken.None);

        capture.Commands.Should().ContainSingle().Which.Should().NotContain("project_data_sources");
    }

    // The run must have recorded data sources, at least one, and some allowed project must hold each of them.
    private static void RestrictedPredicateOf(string sql)
    {
        var where = sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];
        where.Should().Contain("q.data_source_ids IS NOT NULL");
        where.Should().Contain("cardinality(q.data_source_ids) > 0");
        where.Should().Contain("unnest(@");
        where.Should().Contain("unnest(q.data_source_ids)");
        where.Should().Contain("FROM beacon.project_data_sources");
        where.Should().Contain("NOT EXISTS");
    }

    private static IEnumerable<TestCaseData> CallersThatReadNoRun()
    {
        yield return new TestCaseData(null).SetName("{m}(no request)");
        yield return new TestCaseData(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ext-1")])))
            .SetName("{m}(anonymous)");
        yield return new TestCaseData(Principal(("auth_method", "api_key"), ("scope", "Read"))).SetName("{m}(API key without projects)");
        yield return new TestCaseData(Principal(("auth_method", "mcp_caller"), ("scope", "Read"))).SetName("{m}(MCP caller without projects)");
        yield return new TestCaseData(ApiKey("")).SetName("{m}(blank project restriction)");
    }

    private static IEnumerable<string> RecordedComments()
    {
        yield return NotificationFailureReasons.Generic;
        yield return NotificationFailureReasons.TimedOut;
        yield return NotificationFailureReasons.DestinationNotAllowed;
        yield return NotificationFailureReasons.ForStatus(HttpStatusCode.BadGateway);
        yield return RecordedComment;
        yield return $"Recipient 3: {NotificationFailureReasons.ConnectionBlocked} Recipient : {NotificationFailureReasons.Generic} Recipient 12: {NotificationFailureReasons.ForStatus(HttpStatusCode.NotFound)}";
    }

    private static GetNotificationDetailHandler DetailHandler(ClaimsPrincipal caller)
    {
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = caller } };

        return new GetNotificationDetailHandler(CreateService(Factory()), accessor);
    }

    private static async Task<WebApplication> StartAsync(ClaimsPrincipal caller)
    {
        var accessor = new HttpContextAccessor();
        var service = CreateService(Factory());

        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<GetNotificationDetailQuery>(), It.IsAny<CancellationToken>()))
            .Returns((GetNotificationDetailQuery q, CancellationToken ct) => new GetNotificationDetailHandler(service, accessor).Handle(q, ct));
        mediator
            .Setup(x => x.Send(It.IsAny<GetNotificationsQuery>(), It.IsAny<CancellationToken>()))
            .Returns((GetNotificationsQuery q, CancellationToken ct) => new GetNotificationsHandler(service, accessor).Handle(q, ct));
        mediator
            .Setup(x => x.Send(It.IsAny<GetImportedDocumentsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetImportedDocumentsResult([]));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(mediator.Object);
        builder.Services.AddSingleton<IHttpContextAccessor>(accessor);
        builder.Services.AddRouting();
        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            context.User = caller;
            await next(context);
        });
        var api = app.MapGroup("/beacon/api");
        api.MapNotificationsEndpoints();
        api.MapProjectsEndpoints();
        await app.StartAsync();

        return app;
    }

    // The principal ApiKeyAuthMiddleware produces for a Read-scoped key restricted to the given projects.
    private static ClaimsPrincipal ApiKey(string allowedProjects)
    {
        return Principal(("auth_method", "api_key"), ("scope", "Read"), ("allowed_projects", allowedProjects));
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(claims.Select(x => new Claim(x.Type, x.Value)), "Test"));
    }

    private static NotificationService CreateService(IDbContextFactory<BeaconContext> factory)
    {
        return new NotificationService(
            factory,
            adapterFactory: null!,
            secretProtector: null!,
            destinationPolicy: null!,
            Options.Create(new NotificationChannelOptions()),
            NullLogger<NotificationService>.Instance);
    }

    private static IDbContextFactory<BeaconContext> Factory()
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(Sets(), []));

        return factory.Object;
    }

    // Project 1 holds its own source and the shared one, project 2 its own and the shared one, project 3 both projects'
    // own sources. Runs record the sources they read; run 5 was recorded before sources were kept.
    private static Dictionary<Type, object> Sets()
    {
        var runs = new List<QueryExecutionHistory>
        {
            Run(1, LegacyComment, ProjectOneSource),
            Run(2, null, ProjectTwoSource),
            Run(3, null, SharedSource),
            Run(4, null, UnassignedSource),
            Run(5, null, null),
            Run(6, null, ProjectOneSource, ProjectTwoSource),
            Run(7, null, ProjectOneSource, SharedSource),
            Run(8, null, ProjectOneSource, UnassignedSource),
            Run(9, null, UnassignedSource, ProjectOneSource),
            Run(10, null),
            Run(11, RecordedComment, ProjectOneSource)
        };
        var memberships = new List<ProjectDataSource>
        {
            new() { ProjectId = ProjectOne, DataSourceId = ProjectOneSource },
            new() { ProjectId = ProjectOne, DataSourceId = SharedSource },
            new() { ProjectId = ProjectTwo, DataSourceId = ProjectTwoSource },
            new() { ProjectId = ProjectTwo, DataSourceId = SharedSource },
            new() { ProjectId = ProjectBoth, DataSourceId = ProjectOneSource },
            new() { ProjectId = ProjectBoth, DataSourceId = ProjectTwoSource }
        };

        return new Dictionary<Type, object>
        {
            [typeof(QueryExecutionHistory)] = RecordingBeaconContext.MemorySet(runs, []),
            [typeof(Notification)] = RecordingBeaconContext.MemorySet(new List<Notification>(), []),
            [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(memberships, []),
            [typeof(QueryTask)] = RecordingBeaconContext.MemorySet(new List<QueryTask>(), [])
        };
    }

    private static QueryExecutionHistory Run(int id, string? comment, params int[]? dataSourceIds)
    {
        var query = new Query { Id = 100 + id, Name = $"Report {id}" };
        var subscription = new Subscription
        {
            Id = 200 + id,
            QueryId = query.Id,
            Query = query,
            CronExpression = "0 * * * *",
            StoreResults = true
        };

        return new QueryExecutionHistory
        {
            Id = id,
            SubscriptionId = subscription.Id,
            Subscription = subscription,
            ResultCount = 1,
            CompiledSql = "SELECT iban, customer FROM accounts",
            NotificationStatus = comment == null ? NotificationStatus.NotificationSent : NotificationStatus.Failed,
            ExecutionTimeMs = 12,
            Results = StoredRows,
            Comment = comment,
            DataSourceIds = dataSourceIds
        };
    }
}
