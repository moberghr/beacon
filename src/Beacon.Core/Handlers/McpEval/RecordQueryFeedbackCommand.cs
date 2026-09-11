using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Retention;

namespace Beacon.Core.Handlers.McpEval;

/// <summary>
/// Records a human correctness verdict against an <see cref="Data.Entities.McpQuerySignal"/>. A
/// <see cref="McpUserVerdict.Correct"/> verdict on a project- and data-source-scoped signal that carries
/// SQL auto-promotes it into a golden <see cref="Data.Entities.McpEvalCase"/> (once — promotion is
/// idempotent per source signal). Other verdicts are recorded without promotion. Throws (§9.8) when the
/// signal is missing. R12: explicit feedback content (<see cref="Data.Entities.McpQuerySignal.UserCorrectedSql"/>,
/// <see cref="Data.Entities.McpQuerySignal.FeedbackNote"/>) is persisted only when the project's content-retention
/// decision allows it; promotion into a golden case (which copies the question) is blocked under the content lock.
/// </summary>
internal sealed class RecordQueryFeedbackHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    ISender mediator,
    IContentRetentionPolicy policy,
    ILogger<RecordQueryFeedbackHandler> logger)
    : IRequestHandler<RecordQueryFeedbackCommand>
{
    public async Task Handle(RecordQueryFeedbackCommand request, CancellationToken cancellationToken)
    {
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var signal = await context.McpQuerySignals
            .Where(x => x.Id == request.SignalId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Query signal {request.SignalId} not found.");

        var decision = await ResolveOrLockAsync(signal.ProjectId, request.SignalId, cancellationToken);

        signal.UserVerdict = request.Verdict;
        signal.UserCorrectedSql = decision.AllowExplicitFeedbackContent ? request.CorrectedSql : null;
        signal.FeedbackNote = decision.AllowExplicitFeedbackContent ? request.Note : null;
        await context.SaveChangesAsync(cancellationToken);

        if (request.Verdict != McpUserVerdict.Correct)
        {
            return;
        }

        if (!decision.RetainQueryContent)
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

        // R12: the golden case copies the note into McpEvalCase.Notes, so it must carry the GATED value. Passing
        // request.Note here would persist user text for a project with AllowExplicitFeedbackContent=false even
        // though the same value was just nulled on the signal — and the belt would not catch it, because the belt
        // only fires when the content lock itself is on (review SF-F001).
        await mediator.Send(new PromoteSignalToGoldenCommand(request.SignalId, signal.FeedbackNote), cancellationToken);
    }

    // §1.7 — a settings/cache failure must not escape into the tool and cost us the audit row. Fails CLOSED
    // (no content retained, no promotion), mirroring McpAuditService.RetainsContentAsync and the interceptor.
    private async Task<ContentRetentionDecision> ResolveOrLockAsync(int? projectId, int signalId, CancellationToken cancellationToken)
    {
        try
        {
            return await policy.ResolveAsync(projectId, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Announce it: failing closed here silently drops content the project may well have allowed and skips a
            // legitimate promotion, and the audit row looks identical to a normal call. Identifiers only (§1.11),
            // mirroring McpAuditService.RetainsContentAsync and ContentRetentionInterceptor.ResolveOrLockAsync.
            logger.LogWarning(
                exception,
                "Content retention policy could not be resolved for signal {SignalId} (project {ProjectId}); failing closed.",
                signalId,
                projectId);

            return new ContentRetentionDecision(false, false);
        }
    }
}

public record RecordQueryFeedbackCommand(int SignalId, McpUserVerdict Verdict, string? CorrectedSql = null, string? Note = null) : IRequest;
