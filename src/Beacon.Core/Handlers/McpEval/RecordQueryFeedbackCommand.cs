using MediatR;
using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Handlers.McpEval;

/// <summary>
/// Records a human correctness verdict against an <see cref="Data.Entities.McpQuerySignal"/>. A
/// <see cref="McpUserVerdict.Correct"/> verdict on a project- and data-source-scoped signal that carries
/// SQL auto-promotes it into a golden <see cref="Data.Entities.McpEvalCase"/> (once — promotion is
/// idempotent per source signal). Other verdicts are recorded without promotion. Throws (§9.8) when the
/// signal is missing.
/// </summary>
internal sealed class RecordQueryFeedbackHandler(IDbContextFactory<BeaconContext> contextFactory, ISender mediator)
    : IRequestHandler<RecordQueryFeedbackCommand>
{
    public async Task Handle(RecordQueryFeedbackCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var signal = await context.McpQuerySignals
            .Where(x => x.Id == request.SignalId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Query signal {request.SignalId} not found.");

        signal.UserVerdict = request.Verdict;
        signal.UserCorrectedSql = request.CorrectedSql;
        signal.FeedbackNote = request.Note;
        await context.SaveChangesAsync(cancellationToken);

        if (request.Verdict != McpUserVerdict.Correct)
        {
            return;
        }

        if (signal.ProjectId is null || signal.DataSourceId is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(signal.UserCorrectedSql)
            && string.IsNullOrWhiteSpace(signal.CorrectedSql)
            && string.IsNullOrWhiteSpace(signal.GeneratedSql))
        {
            return;
        }

        var alreadyPromoted = await context.McpEvalCases
            .Where(x => x.SourceSignalId == request.SignalId)
            .AnyAsync(cancellationToken);
        if (alreadyPromoted)
        {
            return;
        }

        await mediator.Send(new PromoteSignalToGoldenCommand(request.SignalId, request.Note), cancellationToken);
    }
}

public record RecordQueryFeedbackCommand(int SignalId, McpUserVerdict Verdict, string? CorrectedSql = null, string? Note = null) : IRequest;
