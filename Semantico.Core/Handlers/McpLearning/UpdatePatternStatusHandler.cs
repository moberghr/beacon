using MediatR;
using Microsoft.EntityFrameworkCore;
using Semantico.Core.Data;
using Semantico.Core.Data.Enums;

namespace Semantico.Core.Handlers.McpLearning;

internal sealed class UpdatePatternStatusHandler(IDbContextFactory<SemanticoContext> contextFactory)
    : IRequestHandler<UpdatePatternStatusCommand>
{
    public async Task Handle(UpdatePatternStatusCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var pattern = await context.McpLearnedPatterns.FindAsync([request.PatternId], cancellationToken)
            ?? throw new InvalidOperationException($"Pattern {request.PatternId} not found");

        pattern.Status = request.NewStatus;
        pattern.ReviewedByUserId = request.ReviewedByUserId;
        pattern.ReviewedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);
    }
}

public record UpdatePatternStatusCommand : IRequest
{
    public required int PatternId { get; init; }
    public required McpPatternStatus NewStatus { get; init; }
    public int? ReviewedByUserId { get; init; }
}
