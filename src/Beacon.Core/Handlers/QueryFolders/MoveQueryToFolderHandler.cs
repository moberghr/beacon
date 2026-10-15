using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Authorization;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.QueryFolders;

/// <summary>Moves a query into a folder (or to the root); the move is recorded as the query's last change, by the caller.</summary>
internal sealed class MoveQueryToFolderHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IBeaconActorAccessor actorAccessor,
    TimeProvider timeProvider)
    : IRequestHandler<MoveQueryToFolderCommand, Unit>
{
    public async Task<Unit> Handle(MoveQueryToFolderCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = await context.Queries
            .Where(q => q.Id == request.QueryId)
            .FirstOrDefaultAsync(cancellationToken);

        if (query == null)
        {
            throw new InvalidOperationException("Query not found.");
        }

        // If moving to a folder, validate it exists
        if (request.FolderId.HasValue)
        {
            var folderExists = await context.QueryFolders
                .AnyAsync(f => f.Id == request.FolderId.Value, cancellationToken);

            if (!folderExists)
            {
                throw new InvalidOperationException("Target folder not found.");
            }
        }

        query.FolderId = request.FolderId;
        query.RecordChangeBy(await actorAccessor.GetCurrentAsync(cancellationToken), timeProvider);

        await context.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}

public record MoveQueryToFolderCommand(
    int QueryId,
    [property: ProjectScopeExempt("query folders are project-less")] int? FolderId  // Null means move to root (no folder)
) : IRequest<Unit>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.Query(QueryId), ProjectAction.Write)
    ];
}
