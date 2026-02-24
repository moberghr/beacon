using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;
using Semantico.Core.Models.DataQuality;

namespace Semantico.Core.Handlers.DataQuality.GetDataQualityOverview;

internal sealed class GetDataQualityOverviewHandler(
    IDbContextFactory<SemanticoContext> contextFactory) : IRequestHandler<GetDataQualityOverviewQuery, List<DataQualityOverviewData>>
{
    public async Task<List<DataQualityOverviewData>> Handle(GetDataQualityOverviewQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var scoresQuery = context.DataQualityScores.AsQueryable();

        if (request.DataSourceId.HasValue)
            scoresQuery = scoresQuery.Where(s => s.DataSourceId == request.DataSourceId.Value);

        var scores = await scoresQuery
            .Select(s => new
            {
                s.DataSourceId,
                DataSourceName = s.DataSource.Name,
                s.SchemaName,
                s.TableName,
                s.Score,
                s.EvaluatedAt,
                s.TrendDirection,
                s.PreviousScore,
                s.Id
            })
            .ToListAsync(cancellationToken);

        var contractCounts = await context.DataContracts
            .Where(c => c.IsEnabled)
            .GroupBy(c => c.DataSourceId)
            .Select(g => new { DataSourceId = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        var grouped = scores
            .GroupBy(s => new { s.DataSourceId, s.DataSourceName })
            .Select(g =>
            {
                var tableScores = g.Select(s => new DataQualityScoreData
                {
                    Id = s.Id,
                    DataSourceId = s.DataSourceId,
                    SchemaName = s.SchemaName,
                    TableName = s.TableName,
                    Score = s.Score,
                    EvaluatedAt = s.EvaluatedAt,
                    TrendDirection = s.TrendDirection,
                    PreviousScore = s.PreviousScore
                }).ToList();

                return new DataQualityOverviewData
                {
                    DataSourceId = g.Key.DataSourceId,
                    DataSourceName = g.Key.DataSourceName,
                    AverageScore = tableScores.Count > 0 ? Math.Round(tableScores.Average(t => t.Score), 2) : 0,
                    TotalTables = tableScores.Count,
                    HealthyTables = tableScores.Count(t => t.Score >= 80),
                    DegradingTables = tableScores.Count(t => t.TrendDirection == DataQualityTrendDirection.Degrading),
                    ActiveContracts = contractCounts.FirstOrDefault(c => c.DataSourceId == g.Key.DataSourceId)?.Count ?? 0,
                    TableScores = tableScores
                };
            })
            .ToList();

        return grouped;
    }
}

public record GetDataQualityOverviewQuery(int? DataSourceId = null) : IRequest<List<DataQualityOverviewData>>;
