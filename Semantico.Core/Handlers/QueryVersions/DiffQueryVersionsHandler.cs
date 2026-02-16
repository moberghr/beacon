using MediatR;
using Semantico.Core.Models.Queries;
using Semantico.Core.Services;

namespace Semantico.Core.Handlers.QueryVersions;

internal sealed class DiffQueryVersionsHandler(IQueryVersionService versionService)
    : IRequestHandler<DiffQueryVersionsQuery, QueryVersionDiff>
{
    public async Task<QueryVersionDiff> Handle(DiffQueryVersionsQuery request, CancellationToken cancellationToken)
    {
        return await versionService.DiffVersionsAsync(request.VersionIdA, request.VersionIdB, cancellationToken);
    }
}

public record DiffQueryVersionsQuery : IRequest<QueryVersionDiff>
{
    public required int VersionIdA { get; init; }
    public required int VersionIdB { get; init; }
}
