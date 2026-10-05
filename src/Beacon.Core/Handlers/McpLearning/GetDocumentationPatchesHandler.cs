using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;

namespace Beacon.Core.Handlers.McpLearning;

internal sealed class GetDocumentationPatchesHandler(IDbContextFactory<BeaconContext> contextFactory)
    : IRequestHandler<GetDocumentationPatchesQuery, PagedList<DocumentationPatchEntry>>
{
    public async Task<PagedList<DocumentationPatchEntry>> Handle(GetDocumentationPatchesQuery request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.McpDocumentationPatches.AsQueryable();

        if (request.ProjectId.HasValue)
            query = query.Where(p => p.ProjectId == request.ProjectId.Value);

        if (request.Status.HasValue)
            query = query.Where(p => p.Status == request.Status.Value);

        return await query
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
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-supportingSignalCount");
    }
}

/// <summary>Best-supported first unless <c>sort</c> says otherwise.</summary>
public record GetDocumentationPatchesQuery : ListRequest, IRequest<PagedList<DocumentationPatchEntry>>
{
    public int? ProjectId { get; init; }
    public McpDocPatchStatus? Status { get; init; }
}

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
