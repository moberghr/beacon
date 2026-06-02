using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Handlers.Queries;

internal sealed class CreateQueryHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<CreateQueryCommand, CreateQueryResult>
{
    public async Task<CreateQueryResult> Handle(CreateQueryCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new InvalidOperationException("Query name is required.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = new Query
        {
            Name = request.Name.Trim(),
            Description = request.Description?.Trim(),
        };

        context.Queries.Add(query);
        await context.SaveChangesAsync(cancellationToken);

        return new CreateQueryResult
        {
            QueryId = query.Id,
        };
    }
}

public record CreateQueryCommand : IRequest<CreateQueryResult>
{
    public required string Name { get; init; }
    public string? Description { get; init; }
}

public record CreateQueryResult
{
    public required int QueryId { get; init; }
}
