using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Beacon.Core.Models.DataQuality;

namespace Beacon.Core.Handlers.DataQuality.GetDataContracts;

internal sealed class GetDataContractsHandler(
    IDbContextFactory<BeaconContext> contextFactory) : IRequestHandler<GetDataContractsQuery, PagedList<DataContractData>>
{
    public async Task<PagedList<DataContractData>> Handle(GetDataContractsQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.DataContracts.AsQueryable();

        if (request.DataSourceId.HasValue)
            query = query.Where(c => c.DataSourceId == request.DataSourceId.Value);

        return await query
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
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-createdTime");
    }
}

/// <summary>Newest first unless <c>sort</c> says otherwise; optionally one data source.</summary>
public record GetDataContractsQuery : ListRequest, IRequest<PagedList<DataContractData>>
{
    public int? DataSourceId { get; init; }
}
