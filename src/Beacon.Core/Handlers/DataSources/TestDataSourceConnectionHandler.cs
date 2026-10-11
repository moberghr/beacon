using Beacon.Core.Data.Enums;
using Beacon.Core.Models.DataSources;
using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class TestDataSourceConnectionHandler(IDataSourceService dataSourceService)
    : IRequestHandler<TestDataSourceConnectionCommand, TestDataSourceConnectionResult>
{
    public async Task<TestDataSourceConnectionResult> Handle(TestDataSourceConnectionCommand request, CancellationToken cancellationToken)
    {
        var data = new DataSourceData
        {
            Name = string.IsNullOrWhiteSpace(request.Name) ? "test" : request.Name,
            DataSourceType = request.DataSourceType,
            DatabaseEngineType = request.DatabaseEngineType,
            ConnectionString = request.ConnectionString,
        };

        var response = await dataSourceService.TestConnectionAsync(data, cancellationToken);

        return new TestDataSourceConnectionResult(response.Success, response.Message);
    }
}

[ProjectScopeExempt("data-source administration, gated by the DataSourceAdmin permission; no stored data source", Policy = BeaconPermissions.DataSourceAdminPolicy)]
public record TestDataSourceConnectionCommand(
    string? Name,
    DataSourceType DataSourceType,
    DatabaseEngineType? DatabaseEngineType,
    string ConnectionString) : IRequest<TestDataSourceConnectionResult>;

public record TestDataSourceConnectionResult(bool Success, string Message);
