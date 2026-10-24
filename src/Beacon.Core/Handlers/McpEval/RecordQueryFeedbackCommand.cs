using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.Services.Retention;
using Beacon.Core.Authorization;

namespace Beacon.Core.Handlers.McpEval;

/// <summary>
/// Records a human correctness verdict against an <see cref="Data.Entities.McpQuerySignal"/>, with the user who recorded it
/// (<see cref="Data.Entities.McpQuerySignal.FeedbackByUserId"/>) and, apart from that attribution, whether a curator
/// recorded it (<see cref="Data.Entities.McpQuerySignal.FeedbackFromCurator"/>): an interactive session holding Curate in
/// the signal's project at that moment — never an API key or an MCP caller, even one a curator owns or acts as. Only a
/// curator's verdict weighs in the learning aggregation. A verdict never creates a golden case: a
/// <see cref="McpUserVerdict.Correct"/> verdict waits in the golden candidates list for an Admin to promote it
/// (<see cref="PromoteSignalToGoldenCommand"/>). Corrected SQL is stored only after <see cref="IGroundingSqlCheck"/> passes
/// it in the signal's data source's dialect, and stored without its comments. The verdict is recorded whatever becomes of
/// the corrected SQL; the SQL is not kept — and the result says so, with the reason
/// (<see cref="RecordQueryFeedbackResult"/>) — when the project's content retention does not allow it, when the answer did
/// not run on a single data source (there is no dialect to check it in), or when the check refuses it.
/// Throws (§9.8) when the signal is missing. Explicit feedback content
/// (<see cref="Data.Entities.McpQuerySignal.UserCorrectedSql"/>, <see cref="Data.Entities.McpQuerySignal.FeedbackNote"/>)
/// is persisted only when the project's content-retention decision allows it. Only the signal's own caller, a
/// ProjectAdmin of its project or an Admin records a verdict on it (403 otherwise).
/// </summary>
internal sealed class RecordQueryFeedbackHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IGroundingSqlCheck groundingSqlCheck,
    IContentRetentionPolicy policy,
    IProjectAccessEvaluator projectAccess,
    IBeaconActorAccessor actorAccessor,
    ILogger<RecordQueryFeedbackHandler> logger)
    : IRequestHandler<RecordQueryFeedbackCommand, RecordQueryFeedbackResult>
{
    internal const string RetentionOffReason = "the project does not retain query content, so corrected SQL is not stored.";

    internal const string NoSingleDataSourceReason =
        "the answer did not run on a single data source, so there is no dialect to check the corrected SQL in.";

    public async Task<RecordQueryFeedbackResult> Handle(RecordQueryFeedbackCommand request, CancellationToken cancellationToken)
    {
        if (request.CorrectedSql?.Length > InputLimits.FeedbackCorrectedSqlMaxLength)
        {
            throw new BeaconValidationException($"The corrected SQL is longer than {InputLimits.FeedbackCorrectedSqlMaxLength} characters.");
        }

        if (request.Note?.Length > InputLimits.FeedbackNoteMaxLength)
        {
            throw new BeaconValidationException($"The note is longer than {InputLimits.FeedbackNoteMaxLength} characters.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var signal = await context.McpQuerySignals
            .Where(x => x.Id == request.SignalId)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Query signal {request.SignalId} not found.");

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        await EnsureMayRateAsync(actor, signal.Id, signal.UserId, signal.ProjectId, cancellationToken);

        var decision = await ResolveOrLockAsync(signal.ProjectId, request.SignalId, cancellationToken);

        var corrected = await CorrectedSqlAsync(request, signal.DataSourceId, decision.AllowExplicitFeedbackContent, cancellationToken);

        var fromCurator = await FromCuratorAsync(actor, signal.ProjectId, cancellationToken);

        // The digest a promotion's claim compares is written with the corrected SQL it digests, in the same statement: set
        // only when that SQL changes, so a digest never goes out without its SQL.
        if (signal.UserCorrectedSql != corrected.Sql)
        {
            signal.CandidateSqlHash = GroundingContentHash.OfCandidateSql(corrected.Sql, signal.CorrectedSql, signal.GeneratedSql);
        }

        signal.UserVerdict = request.Verdict;
        signal.UserCorrectedSql = corrected.Sql;
        signal.FeedbackNote = decision.AllowExplicitFeedbackContent ? request.Note : null;
        signal.FeedbackByUserId = actor.BeaconUserId;
        signal.FeedbackFromCurator = fromCurator;
        await context.SaveChangesAsync(cancellationToken);

        if (corrected.NotRecordedReason != null)
        {
            // Identifiers only (§1.11): neither the SQL nor the check's reason, which may quote it.
            logger.LogInformation("The corrected SQL of feedback on signal {SignalId} was not kept; the verdict was recorded", signal.Id);
        }

        return new RecordQueryFeedbackResult(corrected.Sql != null, corrected.NotRecordedReason);
    }

    // Corrected SQL is checked in the dialect of the one data source the signal ran on, and stored without its comments.
    // When it may not be kept — retention off, no single data source (a cross-source or knowledge answer), refused by the
    // check — the verdict stands and only the SQL is dropped, with the reason for the caller.
    private async Task<(string? Sql, string? NotRecordedReason)> CorrectedSqlAsync(
        RecordQueryFeedbackCommand request,
        int? dataSourceId,
        bool retainsContent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.CorrectedSql))
        {
            return (null, null);
        }

        if (!retainsContent)
        {
            return (null, RetentionOffReason);
        }

        if (dataSourceId == null)
        {
            return (null, NoSingleDataSourceReason);
        }

        var check = await groundingSqlCheck.CheckAsync(dataSourceId.Value, request.CorrectedSql, cancellationToken);

        return check.IsAllowed
            ? (check.Sql, null)
            : (null, $"it did not pass the SQL check: {check.Refusal}");
    }

    // A verdict (and corrected SQL) on someone else's answer is a ProjectAdmin's or an Admin's: the signal's own caller
    // rates it otherwise. A signal recorded without a caller has no own caller.
    private async Task EnsureMayRateAsync(BeaconActor actor, int signalId, int? signalUserId, int? signalProjectId, CancellationToken cancellationToken)
    {
        var scope = await projectAccess.GetScopeAsync(cancellationToken);
        if (scope.IsUnrestricted)
        {
            return;
        }

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

    // A curator's verdict: an interactive session — a signed-in Beacon user, or a host's session — holding Curate in the
    // signal's project now. An API key, an MCP caller and the owner of scheduled work never are, whoever they act for.
    private async Task<bool> FromCuratorAsync(BeaconActor actor, int? projectId, CancellationToken cancellationToken)
    {
        if (projectId == null || actor.Kind is not (BeaconPrincipalKind.User or BeaconPrincipalKind.Session))
        {
            return false;
        }

        var scope = await projectAccess.GetScopeAsync(cancellationToken);

        return scope.Allows(projectId.Value, ProjectAction.Curate);
    }

    // §1.7 — a settings/cache failure must not escape into the tool and cost us the audit row. Fails CLOSED
    // (no content retained), mirroring McpAuditService.RetainsContentAsync and the interceptor.
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
            // Announce it: failing closed here silently drops content the project may well have allowed, and the audit
            // row looks identical to a normal call. Identifiers only (§1.11),
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

public record RecordQueryFeedbackCommand(int SignalId, McpUserVerdict Verdict, string? CorrectedSql = null, string? Note = null) : IRequest<RecordQueryFeedbackResult>, IProjectAuthorizedRequest
{
    IEnumerable<ProjectCheck> IProjectAuthorizedRequest.ProjectChecks =>
    [
        new(ProjectResource.QuerySignal(SignalId), ProjectAction.Query)
    ];
}

/// <param name="CorrectedSqlRecorded">True when corrected SQL was supplied and kept with the verdict.</param>
/// <param name="CorrectedSqlNotRecordedReason">
/// Why supplied corrected SQL was not kept (the verdict was recorded all the same); null when it was kept or none was
/// supplied.
/// </param>
public record RecordQueryFeedbackResult(bool CorrectedSqlRecorded, string? CorrectedSqlNotRecordedReason);
