using Beacon.Core.Models.Metadata;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class GetDataSourceMetadataHandler(IDatabaseMetadataService metadataService)
    : IRequestHandler<GetDataSourceMetadataQuery, DatabaseMetadataSnapshot>
{
    public async Task<DatabaseMetadataSnapshot> Handle(GetDataSourceMetadataQuery request, CancellationToken cancellationToken)
    {
        if (request.DataSourceId <= 0)
        {
            throw new InvalidOperationException("Data source id must be positive.");
        }

        var snapshot = await metadataService.GetMetadataAsync(request.DataSourceId, cancellationToken);

        if (snapshot == null)
        {
            throw new InvalidOperationException($"Data source {request.DataSourceId} metadata not available.");
        }

        return snapshot;
    }
}

public record GetDataSourceMetadataQuery(int DataSourceId) : IRequest<DatabaseMetadataSnapshot>;
