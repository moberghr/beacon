using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data;

namespace Semantico.Core.Handlers.Queries;

internal sealed class ToggleQueryLockHandler(
    IDbContextFactory<SemanticoContext> contextFactory,
    ILogger<ToggleQueryLockHandler> logger)
    : IRequestHandler<ToggleQueryLockCommand, ToggleQueryLockResult>
{
    public async Task<ToggleQueryLockResult> Handle(
        ToggleQueryLockCommand request,
        CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = await context.Queries
            .FirstOrDefaultAsync(q => q.Id == request.QueryId, cancellationToken);

        if (query == null)
        {
            return new ToggleQueryLockResult
            {
                Success = false,
                ErrorMessage = $"Query with ID {request.QueryId} not found"
            };
        }

        if (request.Lock)
        {
            if (query.IsLocked)
            {
                return new ToggleQueryLockResult
                {
                    Success = true,
                    QueryId = query.Id,
                    IsLocked = true,
                    Message = "Query is already locked"
                };
            }

            query.IsLocked = true;
            query.LockedAt = DateTime.UtcNow;
            query.LockedByUserId = request.UserId;

            logger.LogInformation(
                "Query {QueryId} locked by user {UserId}",
                request.QueryId,
                request.UserId);
        }
        else
        {
            if (!query.IsLocked)
            {
                return new ToggleQueryLockResult
                {
                    Success = true,
                    QueryId = query.Id,
                    IsLocked = false,
                    Message = "Query is already unlocked"
                };
            }

            query.IsLocked = false;
            query.LockedAt = null;
            query.LockedByUserId = null;

            logger.LogInformation(
                "Query {QueryId} unlocked by user {UserId}",
                request.QueryId,
                request.UserId);
        }

        await context.SaveChangesAsync(cancellationToken);

        return new ToggleQueryLockResult
        {
            Success = true,
            QueryId = query.Id,
            IsLocked = query.IsLocked,
            Message = query.IsLocked ? "Query locked successfully" : "Query unlocked successfully"
        };
    }
}

public record ToggleQueryLockCommand : IRequest<ToggleQueryLockResult>
{
    public required int QueryId { get; init; }
    public required bool Lock { get; init; }
    public string? UserId { get; init; }
}

public record ToggleQueryLockResult
{
    public bool Success { get; init; }
    public int QueryId { get; init; }
    public bool IsLocked { get; init; }
    public string? Message { get; init; }
    public string? ErrorMessage { get; init; }
}
