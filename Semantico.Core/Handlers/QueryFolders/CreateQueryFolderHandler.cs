using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Entities;

namespace Semantico.Core.Handlers.QueryFolders;

internal sealed class CreateQueryFolderHandler(IDbContextFactory<SemanticoContext> contextFactory)
    : IRequestHandler<CreateQueryFolderCommand, CreateQueryFolderResult>
{
    public async Task<CreateQueryFolderResult> Handle(CreateQueryFolderCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        // Check if folder with same name exists at the same level
        var existingFolder = await context.QueryFolders
            .Where(f => f.ParentFolderId == request.ParentFolderId && f.Name == request.Name)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingFolder != null)
        {
            throw new InvalidOperationException($"A folder named '{request.Name}' already exists at this level.");
        }

        // Calculate path
        string path;
        if (request.ParentFolderId.HasValue)
        {
            var parentFolder = await context.QueryFolders
                .Where(f => f.Id == request.ParentFolderId.Value)
                .FirstOrDefaultAsync(cancellationToken);

            if (parentFolder == null)
            {
                throw new InvalidOperationException("Parent folder not found.");
            }

            path = $"{parentFolder.Path}/{request.Name}";
        }
        else
        {
            path = request.Name;
        }

        // Get next sort order
        var maxSortOrder = await context.QueryFolders
            .Where(f => f.ParentFolderId == request.ParentFolderId)
            .MaxAsync(f => (int?)f.SortOrder, cancellationToken) ?? 0;

        var folder = new QueryFolder
        {
            Name = request.Name,
            Description = request.Description,
            ParentFolderId = request.ParentFolderId,
            Path = path,
            SortOrder = maxSortOrder + 1
        };

        context.QueryFolders.Add(folder);
        await context.SaveChangesAsync(cancellationToken);

        return new CreateQueryFolderResult
        {
            FolderId = folder.Id,
            Path = folder.Path
        };
    }
}

public record CreateQueryFolderCommand(
    string Name,
    string? Description,
    int? ParentFolderId
) : IRequest<CreateQueryFolderResult>;

public record CreateQueryFolderResult
{
    public int FolderId { get; init; }
    public string Path { get; init; } = null!;
}
