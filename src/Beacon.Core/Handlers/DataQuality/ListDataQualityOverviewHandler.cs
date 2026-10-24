using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Models.DataQuality;

namespace Beacon.Core.Handlers.DataQuality.GetDataQualityOverview;

/// <summary>
/// The paged form of <see cref="GetDataQualityOverviewQuery"/>: one card per readable data source that has scores, sorted
/// by data source name unless the request names a sort. The page holds whole data sources; each carries its table counts
/// and average from the database and only its <see cref="TableScoreLimit"/> lowest-scoring tables
/// (<see cref="DataQualityOverviewData.TotalTables"/> still counts them all).
/// </summary>
internal sealed class ListDataQualityOverviewHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess) : IRequestHandler<ListDataQualityOverviewQuery, PagedList<DataQualityOverviewData>>
{
    internal const int TableScoreLimit = 20;

    private const double HealthyScore = 80;

    public async Task<PagedList<DataQualityOverviewData>> Handle(ListDataQualityOverviewQuery request, CancellationToken cancellationToken)
    {
        var readable = (await projectAccess.GetScopeAsync(cancellationToken)).For(ProjectAction.Read);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var page = await context.DataQualityScores
            .WhereSourceAccessible(context, readable, x => x.DataSourceId)
            .WhereIf(request.DataSourceId.HasValue, x => x.DataSourceId == request.DataSourceId!.Value)
            .GroupBy(x => new { x.DataSourceId, DataSourceName = x.DataSource.Name })
            .Select(x =>
                new DataQualityOverviewRow
                {
                    DataSourceId = x.Key.DataSourceId,
                    DataSourceName = x.Key.DataSourceName,
                    AverageScore = x.Average(y => y.Score),
                    TotalTables = x.Count(),
                    HealthyTables = x.Count(y => y.Score >= HealthyScore),
                    DegradingTables = x.Count(y => y.TrendDirection == DataQualityTrendDirection.Degrading)
                })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "dataSourceName", tiebreaker: nameof(DataQualityOverviewRow.DataSourceId));

        var sourceIds = page.Items
            .Select(x => x.DataSourceId)
            .ToList();

        var contractCounts = await context.DataContracts
            .WhereSourceAccessible(context, readable, x => x.DataSourceId)
            .Where(x => sourceIds.Contains(x.DataSourceId))
            .Where(x => x.IsEnabled)
            .GroupBy(x => x.DataSourceId)
            .Select(x =>
                new
                {
                    DataSourceId = x.Key,
                    Count = x.Count(),
                    OwnerNotAuthorized = x.Count(y => y.OwnerNotAuthorizedSince != null)
                })
            .ToListAsync(cancellationToken);

        var tableScores = await context.DataQualityScores
            .Where(x => sourceIds.Contains(x.DataSourceId))
            .Where(x => context.DataQualityScores
                .Count(y => y.DataSourceId == x.DataSourceId && (y.Score < x.Score || (y.Score == x.Score && y.Id < x.Id))) < TableScoreLimit)
            .OrderBy(x => x.Score)
            .ThenBy(x => x.Id)
            .Select(x =>
                new DataQualityScoreData
                {
                    Id = x.Id,
                    DataSourceId = x.DataSourceId,
                    SchemaName = x.SchemaName,
                    TableName = x.TableName,
                    Score = x.Score,
                    EvaluatedAt = x.EvaluatedAt,
                    TrendDirection = x.TrendDirection,
                    PreviousScore = x.PreviousScore
                })
            .ToListAsync(cancellationToken);

        var overviews = new List<DataQualityOverviewData>();
        foreach (var row in page.Items)
        {
            var contracts = contractCounts.FirstOrDefault(x => x.DataSourceId == row.DataSourceId);

            overviews.Add(new DataQualityOverviewData
            {
                DataSourceId = row.DataSourceId,
                DataSourceName = row.DataSourceName,
                AverageScore = Math.Round(row.AverageScore, 2),
                TotalTables = row.TotalTables,
                HealthyTables = row.HealthyTables,
                DegradingTables = row.DegradingTables,
                ActiveContracts = contracts?.Count ?? 0,
                OwnerNotAuthorizedContracts = contracts?.OwnerNotAuthorized ?? 0,
                TableScores = tableScores
                    .Where(x => x.DataSourceId == row.DataSourceId)
                    .ToList()
            });
        }

        return new PagedList<DataQualityOverviewData>
        {
            Items = overviews,
            TotalCount = page.TotalCount,
            PageCount = page.PageCount
        };
    }
}

public record ListDataQualityOverviewQuery : ListRequest, IRequest<PagedList<DataQualityOverviewData>>, IProjectFilteredRequest
{
    [ProjectScopeExempt("filter only: the list is scope-filtered")]
    public int? DataSourceId { get; init; }
}

internal sealed class DataQualityOverviewRow
{
    public int DataSourceId { get; init; }
    public string DataSourceName { get; init; } = null!;
    public double AverageScore { get; init; }
    public int TotalTables { get; init; }
    public int HealthyTables { get; init; }
    public int DegradingTables { get; init; }
}
