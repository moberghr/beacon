using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;

namespace Semantico.Core.Handlers.QueryFolders;

internal sealed class DeleteQueryFolderHandler(IDbContextFactory<SemanticoContext> contextFactory)
    : IRequestHandler<DeleteQueryFolderCommand, Unit>
{
    public async Task<Unit> Handle(DeleteQueryFolderCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

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
                foreach (var query in folder.Queries)
                {
                    query.FolderId = folder.ParentFolderId;
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
}

public record DeleteQueryFolderCommand(
    int FolderId,
    bool MoveQueriesToParent = false
) : IRequest<Unit>;
