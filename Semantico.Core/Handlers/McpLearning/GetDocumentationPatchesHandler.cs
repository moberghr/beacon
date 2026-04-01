using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Handlers.McpLearning;

internal sealed class GetDocumentationPatchesHandler(IDbContextFactory<SemanticoContext> contextFactory)
    : IRequestHandler<GetDocumentationPatchesQuery, GetDocumentationPatchesResult>
{
    public async Task<GetDocumentationPatchesResult> Handle(GetDocumentationPatchesQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.McpDocumentationPatches.AsQueryable();

        if (request.ProjectId.HasValue)
            query = query.Where(p => p.ProjectId == request.ProjectId.Value);

        if (request.Status.HasValue)
            query = query.Where(p => p.Status == request.Status.Value);

        var items = await query
            .OrderByDescending(p => p.SupportingSignalCount)
            .Select(p => new DocumentationPatchEntry
            {
                Id = p.Id,
                ProjectId = p.ProjectId,
                DataSourceId = p.DataSourceId,
                TargetType = p.TargetType,
                TargetIdentifier = p.TargetIdentifier,
                CurrentContent = p.CurrentContent,
                ProposedContent = p.ProposedContent,
                Reasoning = p.Reasoning,
                SupportingSignalCount = p.SupportingSignalCount,
                Status = p.Status,
                CreatedTime = p.CreatedTime,
                AppliedAt = p.AppliedAt
            })
            .ToListAsync(cancellationToken);

        return new GetDocumentationPatchesResult(items);
    }
}

public record GetDocumentationPatchesQuery : IRequest<GetDocumentationPatchesResult>
{
    public int? ProjectId { get; init; }
    public McpDocPatchStatus? Status { get; init; }
}

public record GetDocumentationPatchesResult(List<DocumentationPatchEntry> Patches);

public record DocumentationPatchEntry
{
    public int Id { get; init; }
    public int ProjectId { get; init; }
    public int DataSourceId { get; init; }
    public McpDocPatchTarget TargetType { get; init; }
    public string TargetIdentifier { get; init; } = "";
    public string? CurrentContent { get; init; }
    public string ProposedContent { get; init; } = "";
    public string Reasoning { get; init; } = "";
    public int SupportingSignalCount { get; init; }
    public McpDocPatchStatus Status { get; init; }
    public DateTime CreatedTime { get; init; }
    public DateTime? AppliedAt { get; init; }
}
