using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;

namespace Beacon.Core.Handlers.McpGlossary;

/// <summary>
/// Creates an admin-managed business glossary term for a project (optionally scoped to a data source).
/// The term is embedded on the next glossary re-index (the Warp doc-chunk/glossary job) and its definition
/// is injected into the smart context when it is near a masked question (Tier-3 ⑪). Throws (§9.8) when the
/// term or definition is blank — a blank term produces a useless embedding.
/// </summary>
internal sealed class CreateGlossaryTermHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<CreateGlossaryTermCommand, CreateGlossaryTermResult>
{
    public async Task<CreateGlossaryTermResult> Handle(CreateGlossaryTermCommand request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Term))
        {
            throw new InvalidOperationException("Glossary term text is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Definition))
        {
            throw new InvalidOperationException("Glossary term definition is required.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var term = new McpGlossaryTerm
        {
            ProjectId = request.ProjectId,
            DataSourceId = request.DataSourceId,
            Term = request.Term,
            Synonyms = request.Synonyms,
            Definition = request.Definition,
            TargetSchema = request.TargetSchema,
            TargetTable = request.TargetTable,
            TargetColumn = request.TargetColumn,
            MetricExpression = request.MetricExpression,
            IsActive = true
        };

        context.McpGlossaryTerms.Add(term);
        await context.SaveChangesAsync(cancellationToken);

        return new CreateGlossaryTermResult(term.Id);
    }
}

public record CreateGlossaryTermCommand : IRequest<CreateGlossaryTermResult>
{
    public required int ProjectId { get; init; }
    public int? DataSourceId { get; init; }
    public required string Term { get; init; }
    public string? Synonyms { get; init; }
    public required string Definition { get; init; }
    public string? TargetSchema { get; init; }
    public string? TargetTable { get; init; }
    public string? TargetColumn { get; init; }
    public string? MetricExpression { get; init; }
}

public record CreateGlossaryTermResult(int Id);
