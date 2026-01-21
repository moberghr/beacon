using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;

namespace Semantico.Core.Handlers.QueryFolders;

internal sealed class UpdateQueryFolderHandler(IDbContextFactory<SemanticoContext> contextFactory)
    : IRequestHandler<UpdateQueryFolderCommand, Unit>
{
    public async Task<Unit> Handle(UpdateQueryFolderCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

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

    private async Task UpdateFolderPaths(SemanticoContext context, Data.Entities.QueryFolder folder, string newName, CancellationToken cancellationToken)
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

public record UpdateQueryFolderCommand(
    int FolderId,
    string Name,
    string? Description
) : IRequest<Unit>;
