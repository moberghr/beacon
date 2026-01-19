using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Semantico.Core.Data;
using Semantico.Core.Models;

namespace Semantico.Core.Handlers.Ai.ArchiveDocumentation;

internal sealed class ArchiveDocumentationHandler
    : IRequestHandler<ArchiveDocumentationCommand, Unit>
{
    private readonly IDbContextFactory<SemanticoContext> _contextFactory;
    private readonly ILogger<ArchiveDocumentationHandler> _logger;

    public ArchiveDocumentationHandler(
        IDbContextFactory<SemanticoContext> contextFactory,
        ILogger<ArchiveDocumentationHandler> logger)
    {
        _contextFactory = contextFactory;
        _logger = logger;
    }

    public async Task<Unit> Handle(
        ArchiveDocumentationCommand request,
        CancellationToken cancellationToken)
    {
        await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);

        var documentation = await context.DataSourceDocumentations
            .FirstOrDefaultAsync(d => d.Id == request.DocumentationId, cancellationToken);

        if (documentation == null)
        {
            throw new SemanticoException($"Documentation with ID {request.DocumentationId} not found");
        }

        documentation.Archive();
        await context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation(
            "Documentation {DocumentationId} archived",
            request.DocumentationId);

        return Unit.Value;
    }
}

public record ArchiveDocumentationCommand : IRequest<Unit>
{
    public int DocumentationId { get; init; }
}
