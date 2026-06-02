using Beacon.Core.Models.Metadata;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class RefreshDataSourceMetadataHandler(IDatabaseMetadataService metadataService)
    : IRequestHandler<RefreshDataSourceMetadataCommand, DatabaseMetadataSnapshot>
{
    public async Task<DatabaseMetadataSnapshot> Handle(RefreshDataSourceMetadataCommand request, CancellationToken cancellationToken)
    {
        if (request.DataSourceId <= 0)
        {
            throw new InvalidOperationException("Data source id must be positive.");
        }

        return await metadataService.RefreshMetadataAsync(request.DataSourceId, forceRefresh: true, cancellationToken);
    }
}

public record RefreshDataSourceMetadataCommand(int DataSourceId) : IRequest<DatabaseMetadataSnapshot>;
