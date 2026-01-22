using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data;
using Semantico.Core.Models;
using Semantico.Core.Handlers.Ai.UnarchiveDocumentation;

namespace Semantico.AI.Handlers.Ai.UnarchiveDocumentation;

internal sealed class UnarchiveDocumentationHandler
    : IRequestHandler<UnarchiveDocumentationCommand, Unit>
{
    private readonly IDbContextFactory<SemanticoContext> _contextFactory;
    private readonly ILogger<UnarchiveDocumentationHandler> _logger;

    public UnarchiveDocumentationHandler(
        IDbContextFactory<SemanticoContext> contextFactory,
        ILogger<UnarchiveDocumentationHandler> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<Unit> Handle(
        UnarchiveDocumentationCommand request,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var documentation = await context.DataSourceDocumentations
            .FirstOrDefaultAsync(d => d.Id == request.DocumentationId, cancellationToken);

        if (documentation == null)
        {
            throw new SemanticoException($"Documentation with ID {request.DocumentationId} not found");
        }

        documentation.Unarchive();
        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Documentation {DocumentationId} unarchived",
            request.DocumentationId);

        return Unit.Value;
    }
}

