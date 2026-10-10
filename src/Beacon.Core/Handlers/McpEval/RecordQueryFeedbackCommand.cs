using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services.Retention;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.McpEval;

/// <summary>
/// Records a human correctness verdict against an <see cref="Data.Entities.McpQuerySignal"/>. A
/// <see cref="McpUserVerdict.Correct"/> verdict on a project- and data-source-scoped signal that carries
/// SQL auto-promotes it into a golden <see cref="Data.Entities.McpEvalCase"/> (once — promotion is
/// idempotent per source signal). Other verdicts are recorded without promotion. Throws (§9.8) when the
/// signal is missing. R12: explicit feedback content (<see cref="Data.Entities.McpQuerySignal.UserCorrectedSql"/>,
/// <see cref="Data.Entities.McpQuerySignal.FeedbackNote"/>) is persisted only when the project's content-retention
/// decision allows it; promotion into a golden case (which copies the question) is blocked under the content lock.
/// Only the signal's own caller, a ProjectAdmin of its project or an Admin records a verdict on it (403 otherwise);
/// promotion is then sent on from here, after that check.
/// </summary>
internal sealed class RecordQueryFeedbackHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    ISender mediator,
    IContentRetentionPolicy policy,
    IProjectAccessEvaluator projectAccess,
    IBeaconActorAccessor actorAccessor,
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

        await EnsureMayRateAsync(signal.Id, signal.UserId, signal.ProjectId, cancellationToken);

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

    // A verdict (and corrected SQL) on someone else's answer is a ProjectAdmin's or an Admin's: the signal's own caller
    // rates it otherwise. A signal recorded without a caller has no own caller.
    private async Task EnsureMayRateAsync(int signalId, int? signalUserId, int? signalProjectId, CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        if (scope.IsUnrestricted)
        {
            return;
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        var isOwnSignal = signalUserId != null && signalUserId == actor.BeaconUserId;
        var isProjectAdmin = signalProjectId != null && scope.RoleIn(signalProjectId.Value) == ProjectRole.ProjectAdmin;
        if (!isOwnSignal && !isProjectAdmin)
        {
            throw AccessRefusal.Of(
                logger,
                actor,
                "query signal",
                signalId,
                "Only the caller who asked, a project admin of the signal's project or an Admin can record feedback on it.");
        }
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

public record RecordQueryFeedbackCommand(int SignalId, McpUserVerdict Verdict, string? CorrectedSql = null, string? Note = null) : IRequest, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.QuerySignal(SignalId), ProjectAction.Query)
    ];
}
