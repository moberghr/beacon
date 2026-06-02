using MediatR;
using Beacon.Core.Services;

namespace Beacon.Core.Handlers.QueryVersions;

internal sealed class RestoreQueryVersionHandler(IQueryVersionService versionService)
    : IRequestHandler<RestoreQueryVersionCommand, int>
{
    public async Task<int> Handle(RestoreQueryVersionCommand request, CancellationToken cancellationToken)
    {
        return await versionService.RestoreVersionAsync(request.VersionId, request.UserId, cancellationToken);
    }
}

public record RestoreQueryVersionCommand : IRequest<int>
{
    public required int VersionId { get; init; }
    public string? UserId { get; init; }
}
