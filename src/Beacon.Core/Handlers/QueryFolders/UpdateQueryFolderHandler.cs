using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.QueryFolders;

/// <summary>
/// Renames or re-describes a query folder, moving the paths of its descendant folders with a new name.
/// </summary>
internal sealed class UpdateQueryFolderHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    IBeaconActorAccessor actorAccessor,
    ILogger<UpdateQueryFolderHandler> logger)
    : IRequestHandler<UpdateQueryFolderCommand, Unit>
{
    public async Task<Unit> Handle(UpdateQueryFolderCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // A folder is project-less, but its name and description label every query in it and below it: the caller must
        // be able to write every one of them.
        await QueryFolderWriteCheck.DemandWriteOnEveryQueryAsync(context, projectAccess, actorAccessor, logger, request.FolderId, withSubfolders: true, cancellationToken);

        var folder = await context.QueryFolders
            .Where(f => f.Id == request.FolderId)
            .FirstOrDefaultAsync(cancellationToken);

        if (folder == null)
        {
            throw new InvalidOperationException("Folder not found.");
        }

        // Check if renaming would create a duplicate at the same level
        if (folder.Name != request.Name)
        {
            var existingFolder = await context.QueryFolders
                .Where(f => f.ParentFolderId == folder.ParentFolderId && f.Name == request.Name && f.Id != request.FolderId)
                .FirstOrDefaultAsync(cancellationToken);

            if (existingFolder != null)
            {
                throw new InvalidOperationException($"A folder named '{request.Name}' already exists at this level.");
            }

            // Update path for this folder and all descendants
            await UpdateFolderPaths(context, folder, request.Name, cancellationToken);
        }

        folder.Name = request.Name;
        folder.Description = request.Description;

        await context.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }

    private async Task UpdateFolderPaths(BeaconContext context, Data.Entities.QueryFolder folder, string newName, CancellationToken cancellationToken)
    {
        var oldPath = folder.Path;

        // Calculate new path
        string newPath;
        if (folder.ParentFolderId.HasValue)
        {
            var parentFolder = await context.QueryFolders
                .Where(f => f.Id == folder.ParentFolderId.Value)
                .FirstOrDefaultAsync(cancellationToken);

            newPath = $"{parentFolder!.Path}/{newName}";
        }
        else
        {
            newPath = newName;
        }

        folder.Path = newPath;

        // Update all descendant paths
        var descendants = await context.QueryFolders
            .Where(f => f.Path.StartsWith(oldPath + "/"))
            .ToListAsync(cancellationToken);

        foreach (var descendant in descendants)
        {
            descendant.Path = newPath + descendant.Path.Substring(oldPath.Length);
        }
    }
}

[ProjectScopeExempt("query folders are project-less; renaming one demands Write on every query in it and its subfolders (save-time, in the handler)")]
public record UpdateQueryFolderCommand(
    int FolderId,
    string Name,
    string? Description
) : IRequest<Unit>;
