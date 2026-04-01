using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Handlers.McpLearning;

internal sealed class RejectDocumentationPatchHandler(IDbContextFactory<SemanticoContext> contextFactory)
    : IRequestHandler<RejectDocumentationPatchCommand>
{
    public async Task Handle(RejectDocumentationPatchCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var patch = await context.McpDocumentationPatches.FindAsync([request.PatchId], cancellationToken)
            ?? throw new InvalidOperationException($"Patch {request.PatchId} not found");

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
