using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.Queries;

internal sealed class CreateQueryHandler(IDbContextFactory<BeaconContext> contextFactory, IBeaconActorAccessor actorAccessor)
    : IRequestHandler<CreateQueryCommand, CreateQueryResult>
{
    public async Task<CreateQueryResult> Handle(CreateQueryCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidOperationException("Query name is required.");
        }

        // The creator owns the query: while it has no steps, no project holds it and only its owner and Admins reach it.
        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = new Query
        {
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
            OwnerUserId = actor.UserId,
            CreatedByUserId = actor.UserId
        };

        context.Queries.Add(query);
        await context.SaveChangesAsync(cancellationToken);

        return new CreateQueryResult
        {
            QueryId = query.Id,
        };
    }
}

[ProjectScopeExempt("creates a query without steps, so no data source to check; it records its creator as the owner, and its steps are checked when they are saved (UpdateQueryCommand)")]
public record CreateQueryCommand : IRequest<CreateQueryResult>
{
    public required string Name { get; init; }
    public string? Description { get; init; }
}

public record CreateQueryResult
{
    public required int QueryId { get; init; }
}
