using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Models.DataQuality;

namespace Beacon.Core.Handlers.DataQuality.GetDataContracts;

internal sealed class GetDataContractsHandler(
    IDbContextFactory<BeaconContext> contextFactory) : IRequestHandler<GetDataContractsQuery, List<DataContractData>>
{
    public async Task<List<DataContractData>> Handle(GetDataContractsQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.DataContracts.AsQueryable();

        if (request.DataSourceId.HasValue)
            query = query.Where(c => c.DataSourceId == request.DataSourceId.Value);

        return await query
            .OrderByDescending(c => c.CreatedTime)
            .Select(c => new DataContractData
            {
                Id = c.Id,
                DataSourceId = c.DataSourceId,
                DataSourceName = c.DataSource.Name,
                SchemaName = c.SchemaName,
                TableName = c.TableName,
                Name = c.Name,
                Description = c.Description,
                CronExpression = c.CronExpression,
                IsEnabled = c.IsEnabled,
                OwnerUserId = c.OwnerUserId,
                AlertOnFailure = c.AlertOnFailure,
                FailureThresholdScore = c.FailureThresholdScore,
                CreatedTime = c.CreatedTime,
                LatestScore = context.DataQualityScores
                    .Where(s => s.DataSourceId == c.DataSourceId &&
                                s.SchemaName == c.SchemaName &&
                                s.TableName == c.TableName)
                    .Select(s => (double?)s.Score)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);
    }
}

public record GetDataContractsQuery(int? DataSourceId = null) : IRequest<List<DataContractData>>;
