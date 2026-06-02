using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class GetDataSourcesHandler(IDataSourceService dataSourceService)
    : IRequestHandler<GetDataSourcesQuery, GetDataSourcesResult>
{
    public async Task<GetDataSourcesResult> Handle(GetDataSourcesQuery request, CancellationToken cancellationToken)
    {
        var data = await dataSourceService.GetDataSources(null, cancellationToken);

        var entries = data
            .Select(x =>
                new DataSourceEntry(
                    x.Id,
                    x.Name,
                    x.DataSourceType.ToString(),
                    x.DatabaseEngineType?.ToString(),
                    x.Queries.Count,
                    x.MigrationJobsCount,
                    x.MetadataLoadingEnabled))
            .ToList();

        return new GetDataSourcesResult(entries);
    }
}

public record GetDataSourcesQuery : IRequest<GetDataSourcesResult>;

public record GetDataSourcesResult(List<DataSourceEntry> Entries);

public record DataSourceEntry(
    int Id,
    string Name,
    string DataSourceType,
    string? DatabaseEngineType,
    int QueryCount,
    int MigrationJobsCount,
    bool MetadataLoadingEnabled);
