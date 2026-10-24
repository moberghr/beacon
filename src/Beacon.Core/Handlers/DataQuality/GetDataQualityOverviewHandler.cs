using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models.DataQuality;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Helpers;

namespace Beacon.Core.Handlers.DataQuality.GetDataQualityOverview;

/// <summary>
/// The unpaged overview: every table score of every readable data source, in the order the scores were stored, read in
/// one set-based query (no query per source) up to <see cref="ListRequest.LegacyUnpagedCap"/> score rows. A cut sets
/// the truncation header. Only the paged twin (<see cref="ListDataQualityOverviewHandler"/>) cuts a source's tables.
/// </summary>
internal sealed class GetDataQualityOverviewHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    LegacyListTruncation? truncation = null,
    ILogger<GetDataQualityOverviewHandler>? logger = null) : IRequestHandler<GetDataQualityOverviewQuery, List<DataQualityOverviewData>>
{
    public async Task<List<DataQualityOverviewData>> Handle(GetDataQualityOverviewQuery request, CancellationToken cancellationToken)
    {
        var readable = (await projectAccess.GetScopeAsync(cancellationToken)).For(ProjectAction.Read);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var scores = await context.DataQualityScores
            .WhereSourceAccessible(context, readable, x => x.DataSourceId)
            .WhereIf(request.DataSourceId.HasValue, x => x.DataSourceId == request.DataSourceId!.Value)
            .OrderBy(x => x.Id)
            .Select(x =>
                new
                {
                    x.DataSourceId,
                    DataSourceName = x.DataSource.Name,
                    x.SchemaName,
                    x.TableName,
                    x.Score,
                    x.EvaluatedAt,
                    x.TrendDirection,
                    x.PreviousScore,
                    x.Id
                })
            .Take(ListRequest.LegacyUnpagedCap + 1)
            .ToListAsync(cancellationToken);

        scores = LegacyListTruncation.Apply(scores, truncation, logger, "data quality overview");

        var contractCounts = await context.DataContracts
            .WhereSourceAccessible(context, readable, x => x.DataSourceId)
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

        var overviews = new List<DataQualityOverviewData>();
        foreach (var group in scores.GroupBy(x => new { x.DataSourceId, x.DataSourceName }))
        {
            var tableScores = group
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
                .ToList();

            var contracts = contractCounts.FirstOrDefault(x => x.DataSourceId == group.Key.DataSourceId);

            overviews.Add(new DataQualityOverviewData
            {
                DataSourceId = group.Key.DataSourceId,
                DataSourceName = group.Key.DataSourceName,
                AverageScore = Math.Round(tableScores.Average(x => x.Score), 2),
                TotalTables = tableScores.Count,
                HealthyTables = tableScores.Count(x => x.Score >= 80),
                DegradingTables = tableScores.Count(x => x.TrendDirection == DataQualityTrendDirection.Degrading),
                ActiveContracts = contracts?.Count ?? 0,
                OwnerNotAuthorizedContracts = contracts?.OwnerNotAuthorized ?? 0,
                TableScores = tableScores
            });
        }

        return overviews;
    }
}

public record GetDataQualityOverviewQuery([property: ProjectScopeExempt("filter only: the list is scope-filtered")] int? DataSourceId = null) : IRequest<List<DataQualityOverviewData>>, IProjectFilteredRequest;
