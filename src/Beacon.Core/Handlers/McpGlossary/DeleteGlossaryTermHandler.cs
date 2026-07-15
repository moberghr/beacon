using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Core.Handlers.McpGlossary;

/// <summary>
/// Removes a glossary term via SOFT deactivation — sets <c>IsActive = false</c> rather than hard-deleting
/// the row. This matches glossary semantics: a deactivated definition drops out of retrieval/injection
/// (the smart-context block loads active terms only) and its embedding is pruned on the next re-index,
/// while the governed history of the definition is preserved. Throws (§9.8) when the term does not exist.
/// </summary>
internal sealed class DeleteGlossaryTermHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<DeleteGlossaryTermCommand>
{
    public async Task Handle(DeleteGlossaryTermCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var term = await context.McpGlossaryTerms
            .Where(x => x.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Glossary term {request.Id} not found.");

        term.IsActive = false;

        await context.SaveChangesAsync(cancellationToken);
    }
}

public record DeleteGlossaryTermCommand(int Id) : IRequest;
