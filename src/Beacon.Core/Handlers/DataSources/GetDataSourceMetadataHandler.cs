using Beacon.Core.Models.Metadata;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class GetDataSourceMetadataHandler(
    DatabaseMetadataService metadataService,
    MetadataSampleView sampleView)
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

        // Stored sample values go to an Admin only, under the guardrails that apply now.
        return await sampleView.ApplyAsync(snapshot, cancellationToken);
    }
}

/// <summary>
/// A data source's stored catalog. Stored sample values are returned to an Admin only, screened with the guardrails that
/// apply now; every other caller gets the columns without them.
/// </summary>
public record GetDataSourceMetadataQuery(int DataSourceId) : IRequest<DatabaseMetadataSnapshot>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.DataSource(DataSourceId), ProjectAction.Read)
    ];
}
