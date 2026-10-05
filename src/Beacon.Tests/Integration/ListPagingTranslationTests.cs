using Beacon.Core.Handlers.AiActors;
using Beacon.Core.Handlers.DataSources;
using Beacon.Core.Handlers.Queries;
using Beacon.Core.Handlers.Subscriptions;
using Beacon.Core.Handlers.Users;
using Beacon.Core.Helpers;
using Beacon.Core.Models.ControlTower;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Beacon.Tests.Integration;

/// <summary>
/// Server-side paging must reach the database as ORDER BY + LIMIT/OFFSET — the generic property-name sort
/// over a projected DTO, and Control Tower's computed sorts over its stats row (its real query builder).
/// </summary>
[TestFixture]
public class ListPagingTranslationTests
{
    [Test]
    public void GenericSort_OnProjectedDtoWithNavigation_Translates()
    {
        using var context = NpgsqlTestContext.Create();
        var request = new TestRequest { Page = 2, PageSize = 20, Sort = "-queryName" };

        var sql = context.QueryExecutionHistory
            .Select(x =>
                new HistoryRow
                {
                    Id = x.Id,
                    QueryName = x.Subscription.Query.Name,
                    CreatedTime = x.CreatedTime,
                })
            .ApplyListRequest(request, defaultSort: "-createdTime")
            .ToQueryString();

        sql.Should().Contain("ORDER BY").And.Contain("DESC");
        sql.Should().MatchRegex(@"(?is)ORDER BY .*name.* DESC, .*\.id");
        sql.Should().Contain("LIMIT").And.Contain("OFFSET");
    }

    [TestCase(null)]
    [TestCase("queryName")]
    [TestCase("-successRate")]
    [TestCase("totalExecutions")]
    [TestCase("-unresolvedTaskCount")]
    [TestCase("anomalyCount30Days")]
    public void ControlTowerSort_Translates(string? sort)
    {
        using var context = NpgsqlTestContext.Create();
        var request = new GetControlTowerDataRequest { Sort = sort, PageSize = 50 };

        var sql = ControlTowerService
            .ApplySort(ControlTowerService.BuildSubscriptionStatsQuery(context, request, DateTime.UtcNow.AddDays(-30)), request.SortCriteria())
            .Skip(request.PageOrDefault * request.PageSizeOrDefault)
            .Take(request.PageSizeOrDefault)
            .ToQueryString();

        sql.Should().Contain("ORDER BY").And.Contain("LIMIT");
    }

    /// <summary>Mirrors <c>GetDataSourcesHandler</c>: distinct query count and a correlated migration count.</summary>
    [Test]
    public void DataSourcesList_Translates()
    {
        using var context = NpgsqlTestContext.Create();
        var request = new GetDataSourcesQuery { Search = "prod", DatabaseOnly = true, Sort = "-queryCount" };

        var sql = context.DataSources
            .WhereIf(!string.IsNullOrWhiteSpace(request.Search), x => x.Name.Contains(request.Search!))
            .WhereIf(request.DatabaseOnly == true, x => x.DatabaseEngineType != null)
            .Select(x =>
                new DataSourceEntry
                {
                    Id = x.Id,
                    Name = x.Name,
                    DataSourceType = x.DataSourceType.ToString(),
                    DatabaseEngineType = x.DatabaseEngineType.HasValue ? x.DatabaseEngineType.Value.ToString() : null,
                    QueryCount = x.QuerySteps
                        .Where(y => y.Query.ArchivedTime == null)
                        .Select(y => y.QueryId)
                        .Distinct()
                        .Count(),
                    MigrationJobsCount = context.MigrationJobs
                        .Where(y => y.DataSourceId == x.Id || y.DestinationDataSourceId == x.Id)
                        .Count(),
                    MetadataLoadingEnabled = x.MetadataLoadingEnabled,
                })
            .ApplyListRequest(request, defaultSort: "name")
            .ToQueryString();

        sql.Should().Contain("DISTINCT").And.Contain("ORDER BY").And.Contain("LIMIT");
    }

    /// <summary>Mirrors <c>GetAiActorListHandler</c>: the instructions preview is cut in SQL.</summary>
    [Test]
    public void AiActorList_Translates()
    {
        using var context = NpgsqlTestContext.Create();
        var request = new GetAiActorListQuery { DataSourceId = 3, Sort = "name" };

        var sql = context.AiActors
            .Where(x => x.DataSourceId == request.DataSourceId)
            .Select(x =>
                new AiActorListItem
                {
                    ActorId = x.Id,
                    Name = x.Name,
                    Instructions = x.Instructions.Length > 100 ? x.Instructions.Substring(0, 100) + "..." : x.Instructions,
                    DataSourceId = x.DataSourceId,
                    DataSourceName = x.DataSource != null ? x.DataSource.Name : "Unknown",
                    Status = x.Status,
                    ThinkCount = x.ThinkCount,
                    LastThinkTime = x.LastThinkTime,
                    TotalCost = x.TotalCost,
                    CreatedTime = x.CreatedTime
                })
            .ApplyListRequest(request, defaultSort: "-createdTime", tiebreaker: "actorId")
            .ToQueryString();

        sql.Should().Contain("substring").And.Contain("ORDER BY");
    }

    /// <summary>Mirrors <c>GetQueryChangeHistoryHandler</c>: filtered through the step navigation, not a prefetched id list.</summary>
    [Test]
    public void QueryChangeHistory_Translates()
    {
        using var context = NpgsqlTestContext.Create();
        var request = new GetQueryChangeHistoryQuery { QueryId = 5, Page = 1 };

        var sql = context.QueryStepChangeHistory
            .Where(c => c.QueryStep.QueryId == request.QueryId)
            .Select(c =>
                new QueryChangeHistoryItem
                {
                    Id = c.Id,
                    QueryStepId = c.QueryStepId,
                    QueryStepName = c.QueryStep.Name,
                    QueryStepOrder = c.QueryStep.StepOrder,
                    AiActorName = c.AiActor != null ? c.AiActor.Name : null,
                    PreviousSql = c.PreviousSql,
                    NewSql = c.NewSql,
                    ChangeSource = c.ChangeSource,
                    ChangedAt = c.ChangedAt
                })
            .ApplyListRequest(request, defaultSort: "-changedAt")
            .ToQueryString();

        sql.Should().Contain("ORDER BY").And.Contain("OFFSET");
    }

    /// <summary>Mirrors <c>GetUsersHandler</c>: search across three columns, roles as a nested list.</summary>
    [Test]
    public void UsersList_Translates()
    {
        using var context = NpgsqlTestContext.Create();
        var request = new GetUsersQuery { Search = "ana", Sort = "-lastLoginAt" };

        var sql = context.Users
            .Where(x =>
                x.UserName.Contains(request.Search) ||
                (x.Email != null && x.Email.Contains(request.Search)) ||
                (x.DisplayName != null && x.DisplayName.Contains(request.Search)))
            .Select(x =>
                new UserEntry
                {
                    Id = x.Id,
                    UserName = x.UserName,
                    Email = x.Email,
                    LastLoginAt = x.LastLoginAt,
                    Roles = x.UserRoles
                        .Select(y => new UserRoleEntry(y.Role.Id, y.Role.Name, y.Role.Level))
                        .ToList(),
                })
            .ApplyListRequest(request, defaultSort: "userName")
            .ToQueryString();

        sql.Should().Contain("ORDER BY").And.Contain("LIMIT");
    }

    /// <summary>
    /// Mirrors <c>GetSubscriptionsHandler</c>'s archived view: soft-delete filters are dropped for the whole query, so
    /// only archived subscriptions are selected and the recipient count/names re-apply the recipient filter themselves.
    /// </summary>
    [Test]
    public void ArchivedSubscriptionsList_Translates()
    {
        using var context = NpgsqlTestContext.Create();
        var request = new GetSubscriptionsQuery { Archived = true, Sort = "-recipientCount" };

        var sql = context.Subscriptions
            .IgnoreQueryFilters()
            .Where(x => x.ArchivedTime != null)
            .Select(x =>
                new SubscriptionEntry
                {
                    Id = x.Id,
                    QueryName = x.Query.Name,
                    RecipientCount = x.Recipients.Count(y => y.ArchivedTime == null),
                    RecipientNames = x.Recipients
                        .Where(y => y.ArchivedTime == null)
                        .Select(y => y.Name)
                        .ToList(),
                })
            .ApplyListRequest(request, defaultSort: "queryName")
            .ToQueryString();

        sql.Should().MatchRegex(@"(?is)WHERE .*\.archived_time IS NOT NULL");
        sql.Should().MatchRegex(@"(?is)\(\s*SELECT count\(\*\).*archived_time IS NULL");
        sql.Should().Contain("ORDER BY").And.Contain("LIMIT");
    }

    private sealed class HistoryRow
    {
        public int Id { get; init; }

        public string QueryName { get; init; } = string.Empty;

        public DateTime CreatedTime { get; init; }
    }

    private sealed record TestRequest : ListRequest;
}
