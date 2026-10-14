using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Authorization;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.McpLearning;

internal sealed class RejectDocumentationPatchHandler(IDbContextFactory<BeaconContext> contextFactory, ISecurityAuditRecorder securityAudit)
    : IRequestHandler<RejectDocumentationPatchCommand>
{
    public async Task Handle(RejectDocumentationPatchCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var patch = await context.McpDocumentationPatches.FindAsync([request.PatchId], cancellationToken)
            ?? throw new InvalidOperationException($"Patch {request.PatchId} not found");

        if (patch.Status != McpDocPatchStatus.Proposed)
            throw new InvalidOperationException($"Patch {request.PatchId} is already {patch.Status}");

        patch.Status = McpDocPatchStatus.Rejected;
        patch.AppliedByUserId = request.RejectedByUserId;
        patch.AppliedAt = DateTime.UtcNow;

        await securityAudit.StageAsync(context, DocumentationPatchAudit.Decided(SecurityAuditActions.DocumentationPatchRejected, patch), cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }
}

public record RejectDocumentationPatchCommand : IRequest, IProjectAuthorizedRequest
{
    public required int PatchId { get; init; }
    [ProjectScopeExempt("the caller, set by the endpoint; not a project resource")]
    public int? RejectedByUserId { get; init; }

    // Who may apply a patch may reject it (ApplyDocumentationPatchCommand).
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        ProjectCheck.CurateShared(ProjectResource.DocumentationPatch(PatchId))
    ];
}
