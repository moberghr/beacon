using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;

namespace Beacon.Core.Handlers.McpGlossary;

/// <summary>
/// Edits a glossary term — its text, synonyms, definition, target column/metric, data-source scope, or
/// active flag. Only the fields supplied on the command are applied; the rest are left untouched. Throws
/// (§9.8) when the term does not exist. The next glossary re-index re-embeds the changed text.
/// </summary>
internal sealed class UpdateGlossaryTermHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<UpdateGlossaryTermCommand>
{
    public async Task Handle(UpdateGlossaryTermCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var term = await context.McpGlossaryTerms
            .Where(x => x.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Glossary term {request.Id} not found.");

        // null = leave unchanged; but a supplied blank for a required field is invalid (Create rejects
        // blank too — don't let a PUT silently blank Term/Definition).
        if (request.Term is not null && string.IsNullOrWhiteSpace(request.Term))
        {
            throw new InvalidOperationException("Glossary term text cannot be blank.");
        }

        if (request.Definition is not null && string.IsNullOrWhiteSpace(request.Definition))
        {
            throw new InvalidOperationException("Glossary term definition cannot be blank.");
        }

        if (request.Term != null)
        {
            term.Term = request.Term;
        }

        if (request.Synonyms != null)
        {
            term.Synonyms = request.Synonyms;
        }

        if (request.Definition != null)
        {
            term.Definition = request.Definition;
        }

        if (request.TargetSchema != null)
        {
            term.TargetSchema = request.TargetSchema;
        }

        if (request.TargetTable != null)
        {
            term.TargetTable = request.TargetTable;
        }

        if (request.TargetColumn != null)
        {
            term.TargetColumn = request.TargetColumn;
        }

        if (request.MetricExpression != null)
        {
            term.MetricExpression = request.MetricExpression;
        }

        if (request.DataSourceId.HasValue)
        {
            term.DataSourceId = request.DataSourceId.Value;
        }

        if (request.IsActive.HasValue)
        {
            term.IsActive = request.IsActive.Value;
        }

        await context.SaveChangesAsync(cancellationToken);
    }
}

public record UpdateGlossaryTermCommand : IRequest
{
    public required int Id { get; init; }
    public int? DataSourceId { get; init; }
    public string? Term { get; init; }
    public string? Synonyms { get; init; }
    public string? Definition { get; init; }
    public string? TargetSchema { get; init; }
    public string? TargetTable { get; init; }
    public string? TargetColumn { get; init; }
    public string? MetricExpression { get; init; }
    public bool? IsActive { get; init; }
}
