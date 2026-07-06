using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Handlers.McpLearning;

internal sealed class RejectDocumentationPatchHandler(IDbContextFactory<BeaconContext> contextFactory)
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

        await context.SaveChangesAsync(cancellationToken);
    }
}

public record RejectDocumentationPatchCommand : IRequest
{
    public required int PatchId { get; init; }
    public int? RejectedByUserId { get; init; }
}
