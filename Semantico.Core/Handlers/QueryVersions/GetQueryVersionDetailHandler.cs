using MediatR;
using Semantico.Core.Models.Queries;
using Semantico.Core.Services;

namespace Semantico.Core.Handlers.QueryVersions;

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
