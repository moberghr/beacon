using MediatR;
using Beacon.Core.Models.Queries;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.QueryVersions;

internal sealed class GetQueryVersionDetailHandler(IQueryVersionService versionService)
    : IRequestHandler<GetQueryVersionDetailQuery, QueryVersionDetail?>
{
    public async Task<QueryVersionDetail?> Handle(GetQueryVersionDetailQuery request, CancellationToken cancellationToken)
    {
        return await versionService.GetVersionDetailAsync(request.VersionId, cancellationToken);
    }
}

public record GetQueryVersionDetailQuery : IRequest<QueryVersionDetail?>
{
    public required int VersionId { get; init; }
}
