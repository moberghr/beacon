using Beacon.Core.Services;
using MediatR;

namespace Beacon.Core.Handlers.DataSources;

internal sealed class DeleteDataSourceHandler(IDataSourceService dataSourceService)
    : IRequestHandler<DeleteDataSourceCommand>
{
    public async Task Handle(DeleteDataSourceCommand request, CancellationToken cancellationToken)
    {
        await dataSourceService.DeleteDataSource(request.DataSourceId, cancellationToken);
    }
}

public record DeleteDataSourceCommand(int DataSourceId) : IRequest;
