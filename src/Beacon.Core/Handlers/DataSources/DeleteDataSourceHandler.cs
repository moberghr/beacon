using Beacon.Core.Services;
using MediatR;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class DeleteDataSourceHandler(DataSourceService dataSourceService)
    : IRequestHandler<DeleteDataSourceCommand>
{
    public async Task Handle(DeleteDataSourceCommand request, CancellationToken cancellationToken)
    {
        await dataSourceService.DeleteDataSource(request.DataSourceId, cancellationToken);
    }
}

[ProjectScopeExempt("data-source administration, gated by the DataSourceAdmin permission", Policy = BeaconPermissions.DataSourceAdminPolicy)]
public record DeleteDataSourceCommand(int DataSourceId) : IRequest;
