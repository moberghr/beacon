using Beacon.Core.Data.Enums;
using Beacon.Core.Models.DataSources;
using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class CreateDataSourceHandler(IDataSourceService dataSourceService)
    : IRequestHandler<CreateDataSourceCommand, CreateDataSourceResult>
{
    public async Task<CreateDataSourceResult> Handle(CreateDataSourceCommand request, CancellationToken cancellationToken)
    {
        var data = new DataSourceData
        {
            Name = request.Name,
            DataSourceType = request.DataSourceType,
            DatabaseEngineType = request.DatabaseEngineType,
            ConnectionString = request.ConnectionString,
            MetadataLoadingEnabled = request.MetadataLoadingEnabled,
            MetadataMaxTables = request.MetadataMaxTables,
            MetadataMaxColumnsPerTable = request.MetadataMaxColumnsPerTable,
            MetadataLoadTableNamesOnly = request.MetadataLoadTableNamesOnly,
            MetadataExcludeSchemas = request.MetadataExcludeSchemas ?? new List<string>(),
            MetadataIncludeSchemas = request.MetadataIncludeSchemas ?? new List<string>(),
        };

        var response = await dataSourceService.CreateDataSource(data, cancellationToken);

        if (!response.Success)
        {
            throw new InvalidOperationException(response.Message);
        }

        return new CreateDataSourceResult(true, response.Message);
    }
}

public record CreateDataSourceCommand(
    string Name,
    DataSourceType DataSourceType,
    DatabaseEngineType? DatabaseEngineType,
    string ConnectionString,
    bool MetadataLoadingEnabled,
    int MetadataMaxTables,
    int MetadataMaxColumnsPerTable,
    bool MetadataLoadTableNamesOnly,
    List<string>? MetadataExcludeSchemas,
    List<string>? MetadataIncludeSchemas) : IRequest<CreateDataSourceResult>;

public record CreateDataSourceResult(bool Success, string Message);
