using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Helpers;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;

namespace Beacon.Core.Handlers.McpLearning;

/// <summary>
/// The documentation patches of the projects the caller reads, each with whether the caller may apply or reject it
/// (<see cref="GroundingCuration"/>, as the apply and reject routes decide).
/// </summary>
internal sealed class GetDocumentationPatchesHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectAccessEvaluator projectAccess,
    IBeaconPermissionAccessor permissionAccessor)
    : IRequestHandler<GetDocumentationPatchesQuery, PagedList<DocumentationPatchEntry>>
{
    public async Task<PagedList<DocumentationPatchEntry>> Handle(GetDocumentationPatchesQuery request, CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        var readable = scope.For(ProjectAction.Read);
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var query = context.McpDocumentationPatches
            .WhereProjectIn(readable, x => x.ProjectId);

        if (request.ProjectId.HasValue)
            query = query.Where(p => p.ProjectId == request.ProjectId.Value);

        if (request.Status.HasValue)
            query = query.Where(p => p.Status == request.Status.Value);

        var page = await query
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
                AppliedAt = p.AppliedAt,
                AppliedByName = context.Users
                    .Where(x => x.Id == p.AppliedByUserId)
                    .Select(UserLabel.OfUser)
                    .FirstOrDefault()
            })
            .ToPagedListAsync(request, cancellationToken, defaultSort: "-supportingSignalCount");

        // A section patch writes its project's documentation; a description patch its data source's shared metadata.
        var sharedSourceIds = page.Items
            .Where(x => x.TargetType != McpDocPatchTarget.DocumentationSection)
            .Select(x => x.DataSourceId)
            .ToList();
        var shared = await GroundingCuration.OfSharedSourcesAsync(context, scope, permissionAccessor, sharedSourceIds, cancellationToken);

        return page.Map(x => x with
        {
            CanCurate = x.TargetType == McpDocPatchTarget.DocumentationSection
                ? GroundingCuration.MayCurateProject(scope, x.ProjectId)
                : shared.Allows(x.DataSourceId, writesWhereItActs: scope.Allows(x.ProjectId, ProjectAction.Write))
        });
    }
}

/// <summary>Best-supported first unless <c>sort</c> says otherwise.</summary>
public record GetDocumentationPatchesQuery : ListRequest, IRequest<PagedList<DocumentationPatchEntry>>, IProjectFilteredRequest
{
    [ProjectScopeExempt("filter only: the list is scope-filtered")]
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

    /// <summary>When the patch was applied or rejected.</summary>
    public DateTime? AppliedAt { get; init; }

    /// <summary>
    /// Who applied or rejected the patch — display name, or "User {id}" (<see cref="UserLabel"/>); null while it is proposed
    /// or when no user was recorded.
    /// </summary>
    public string? AppliedByName { get; init; }

    /// <summary>
    /// Whether the caller may apply or reject the patch: for a documentation section, Curate in the patch's project; for a
    /// column or table description, Write in the patch's project and Curate in every live project linking its data source,
    /// or the DataSourceAdmin permission.
    /// </summary>
    public bool CanCurate { get; init; }
}
