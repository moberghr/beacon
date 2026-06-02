using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Core.Handlers.QueryFolders;

internal sealed class MoveQueryToFolderHandler(IDbContextFactory<BeaconContext> contextFactory)
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

        await context.SaveChangesAsync(cancellationToken);

        return Unit.Value;
    }
}

public record MoveQueryToFolderCommand(
    int QueryId,
    int? FolderId  // Null means move to root (no folder)
) : IRequest<Unit>;
