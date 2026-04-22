using MediatR;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.DataSources.GetDataSourceSchemas;

internal sealed class GetDataSourceSchemasHandler(
    IDatabaseMetadataService databaseMetadataService)
    : IRequestHandler<GetDataSourceSchemasQuery, GetDataSourceSchemasResult>
{
    public async Task<GetDataSourceSchemasResult> Handle(
        GetDataSourceSchemasQuery request,
        CancellationToken cancellationToken)
    {
        var metadata = await databaseMetadataService.GetMetadataAsync(
            request.DataSourceId,
            cancellationToken);

        var schemas = metadata.Tables
            .Select(t => t.SchemaName)
            .Distinct()
            .OrderBy(s => s)
            .ToList();

        return new GetDataSourceSchemasResult(schemas);
    }
}

public record GetDataSourceSchemasQuery(int DataSourceId) : IRequest<GetDataSourceSchemasResult>;

public record GetDataSourceSchemasResult(List<string> Schemas);
