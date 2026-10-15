using System.Linq.Expressions;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.QueryFolders;

/// <summary>
/// Archives an empty query folder, or one whose queries move to its parent: each moved query records the move as its last
/// change, by the caller.
/// </summary>
internal sealed class DeleteQueryFolderHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    IBeaconActorAccessor actorAccessor,
    TimeProvider timeProvider,
    ILogger<DeleteQueryFolderHandler> logger)
    : IRequestHandler<DeleteQueryFolderCommand, Unit>
{
    public async Task<Unit> Handle(DeleteQueryFolderCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // A folder is project-less, but deleting it moves or strands its queries: the caller must be able to write every
        // one of them.
        await DemandWriteOnEveryQueryAsync(context, request.FolderId, cancellationToken);

        var folder = await context.QueryFolders
            .Include(f => f.ChildFolders)
            .Include(f => f.Queries)
            .Where(f => f.Id == request.FolderId)
            .FirstOrDefaultAsync(cancellationToken);

        if (folder == null)
        {
            throw new InvalidOperationException("Folder not found.");
        }

        // Check if folder has child folders
        if (folder.ChildFolders.Any())
        {
            throw new InvalidOperationException("Cannot delete a folder that contains subfolders. Please delete or move the subfolders first.");
        }

        // Check if folder has queries
        if (folder.Queries.Any())
        {
            if (request.MoveQueriesToParent)
            {
                // Move queries to parent folder (or root if no parent)
                var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
                foreach (var query in folder.Queries)
                {
                    query.FolderId = folder.ParentFolderId;
                    query.RecordChangeBy(actor, timeProvider);
                }
            }
            else
            {
                throw new InvalidOperationException("Cannot delete a folder that contains queries. Set MoveQueriesToParent to true to move queries to the parent folder, or move the queries manually first.");
            }
        }

        // Archive the folder
        folder.Archive();

        await context.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }

    private async Task DemandWriteOnEveryQueryAsync(BeaconContext context, int folderId, CancellationToken cancellationToken)
    {
        var writable = (await projectAccess.GetScopeAsync(cancellationToken)).For(ProjectAction.Write);
        if (writable.Ids == null)
        {
            return;
        }

        var isWritable = ProjectAccessQueryableExtensions.AllSourcesInOneProjectOf<Query>(context, writable, x => x.Steps.Select(y => y.DataSourceId), x => x.OwnerUserId);
        var isNotWritable = Expression.Lambda<Func<Query, bool>>(Expression.Not(isWritable.Body), isWritable.Parameters);
        var holdsUnwritable = await context.Queries
            .Where(x => x.FolderId == folderId)
            .AnyAsync(isNotWritable, cancellationToken);
        if (holdsUnwritable)
        {
            var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

            throw AccessRefusal.Of(logger, actor, "query folder", folderId, "The folder holds queries you cannot change.");
        }
    }
}

[ProjectScopeExempt("query folders are project-less; deleting one demands Write on every query it holds (save-time, in the handler)")]
public record DeleteQueryFolderCommand(
    int FolderId,
    bool MoveQueriesToParent = false
) : IRequest<Unit>;
