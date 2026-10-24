using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Enums;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.ProjectAccess;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.McpEval;

/// <summary>
/// Promotes an answer an Admin reviewed in the golden candidates list into a golden <see cref="McpEvalCase"/>, recording
/// the promoting user (<see cref="McpEvalCase.PromotedByUserId"/>). A promotion applies only to what the Admin reviewed: the
/// command carries the hash of the SQL the candidates list showed (<see cref="GoldenCandidateItem.SqlHash"/>), and the signal
/// must still be rated <see cref="McpUserVerdict.Correct"/> and hold that SQL — the rater's corrected SQL, else the
/// signal's corrected SQL, else its generated SQL — or the promotion is refused (409). Only an answer to a question
/// (<c>ask</c>) becomes a golden case. The gold SQL is stored as <see cref="IGroundingSqlCheck"/> returns it: checked in
/// the data source's dialect, without its comments; a question holding characters a reviewer cannot see is refused.
/// <para>
/// The signal is claimed atomically, in one transaction with the case: a conditional update sets
/// <see cref="McpQuerySignal.PromotedAt"/> only while it is unset and the signal is still rated correct with the reviewed
/// SQL — compared by its digest (<see cref="McpQuerySignal.CandidateSqlHash"/>), which compares exactly under any database
/// collation; a signal recorded before Beacon kept the digest has it stored first, only while it still holds no digest and
/// the SQL read. A promotion that claims nothing reads the signal again: promoted meanwhile, it answers with that case
/// (<see cref="PromoteSignalToGoldenResult.AlreadyPromoted"/>) and creates nothing; changed meanwhile, it is refused (409). A
/// signal promoted before Beacon recorded promotions answers with the first case promoted from it. Throws (§9.8) when the
/// signal is missing, carries no SQL, is not scoped to a project + data source linked to it, or its SQL does not pass the
/// check; refuses (403) a caller without a Beacon user, since who promoted is recorded.
/// </para>
/// </summary>
internal sealed class PromoteSignalToGoldenHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IGroundingSqlCheck groundingSqlCheck,
    IBeaconActorAccessor actorAccessor,
    ISecurityAuditRecorder securityAudit,
    ILogger<PromoteSignalToGoldenHandler> logger)
    : IRequestHandler<PromoteSignalToGoldenCommand, PromoteSignalToGoldenResult>
{
    internal const string ChangedSinceReviewedMessage =
        "The answer changed since it was reviewed (its rating or its SQL): reload the golden candidates and review it again.";

    internal const string UnidentifiedCuratorMessage = "Promoting a golden case needs a signed-in Beacon user: who promoted it is recorded.";

    // The tool of a signal that answers a natural-language question; a query or dry_run signal's "question" is SQL.
    private const string AskTool = "ask";

    public async Task<PromoteSignalToGoldenResult> Handle(PromoteSignalToGoldenCommand request, CancellationToken cancellationToken)
    {
        if (request.Notes?.Length > InputLimits.GoldenNotesMaxLength)
        {
            throw new BeaconValidationException($"The notes are longer than {InputLimits.GoldenNotesMaxLength} characters.");
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (actor.BeaconUserId == null)
        {
            throw AccessRefusal.Of(logger, actor, "query signal", request.SignalId, UnidentifiedCuratorMessage);
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var signal = await SignalAsync(context, request.SignalId, cancellationToken)
            ?? throw new InvalidOperationException($"Query signal {request.SignalId} not found.");

        // Promoting is idempotent: a signal already promoted answers with the case promoted from it, unchanged.
        if (signal.PromotedAt != null || signal.FirstCaseId != null)
        {
            return AlreadyPromoted(signal);
        }

        if (signal.Tool != AskTool)
        {
            throw new InvalidOperationException($"Query signal {request.SignalId} is not an answer to a question: only those become golden cases.");
        }

        if (signal.Verdict != McpUserVerdict.Correct)
        {
            throw new BeaconConflictException(ChangedSinceReviewedMessage);
        }

        if (string.IsNullOrWhiteSpace(signal.Sql))
        {
            throw new InvalidOperationException($"Query signal {request.SignalId} has no SQL to promote.");
        }

        if (!GroundingContentHash.Matches(request.ReviewedSqlHash, GroundingContentHash.OfSql(signal.Sql)))
        {
            throw new BeaconConflictException(ChangedSinceReviewedMessage);
        }

        if (signal.ProjectId is not { } projectId)
        {
            throw new InvalidOperationException($"Query signal {request.SignalId} is not associated with a project.");
        }

        if (signal.DataSourceId is not { } dataSourceId)
        {
            throw new InvalidOperationException($"Query signal {request.SignalId} is not associated with a data source.");
        }

        if (GroundingText.HasHiddenCharacters(signal.Question))
        {
            throw new InvalidOperationException(
                $"Query signal {request.SignalId} cannot become a golden case: its question holds characters that do not show on screen.");
        }

        await ProjectDataSourceLinks.EnsureLinkedAsync(context, projectId, dataSourceId, cancellationToken);

        var check = await groundingSqlCheck.CheckAsync(dataSourceId, signal.Sql, cancellationToken);
        if (!check.IsAllowed)
        {
            throw new InvalidOperationException($"Query signal {request.SignalId} cannot become a golden case: {check.Refusal}");
        }

        var reviewedSqlHash = GroundingContentHash.OfSql(signal.Sql);
        if (signal.SqlHash == null)
        {
            // Recorded before Beacon kept the digest: store the digest of the SQL read, only while the signal still holds no
            // digest and that SQL (every later write of its SQL writes the digest with it).
            var reviewedSql = signal.Sql;
            await context.McpQuerySignals
                .Where(x => x.Id == request.SignalId)
                .Where(x => x.CandidateSqlHash == null)
                .Where(x => (x.UserCorrectedSql ?? x.CorrectedSql ?? x.GeneratedSql) == reviewedSql)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.CandidateSqlHash, reviewedSqlHash), cancellationToken);
        }

        var strategy = context.Database.CreateExecutionStrategy();
        var created = await strategy.ExecuteAsync(async () =>
        {
            context.ChangeTracker.Clear();
            await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

            // The claim: only while unpromoted, still rated correct and still holding the SQL the Admin reviewed, by its
            // digest. A concurrent promotion waits for this row, then claims nothing.
            var claimed = await context.McpQuerySignals
                .Where(x => x.Id == request.SignalId)
                .Where(x => x.PromotedAt == null)
                .Where(x => x.UserVerdict == McpUserVerdict.Correct)
                .Where(x => x.CandidateSqlHash == reviewedSqlHash)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.PromotedAt, DateTime.UtcNow), cancellationToken);
            if (claimed == 0)
            {
                return null;
            }

            var evalCase = new McpEvalCase
            {
                ProjectId = projectId,
                DataSourceId = dataSourceId,
                Question = signal.Question,
                GoldSql = check.Sql,
                SourceSignalId = signal.Id,
                Notes = request.Notes,
                IsActive = true,
                PromotedByUserId = actor.BeaconUserId
            };
            context.McpEvalCases.Add(evalCase);
            await securityAudit.SaveWithEventsAsync(
                context,
                () =>
                [
                    new SecurityAuditEntry(SecurityAuditActions.GoldenCasePromoted)
                        .On(SecurityAuditResources.EvalCase, evalCase.Id)
                        .InProject(projectId)
                        .With(new SecurityAuditDetails()
                            .Add("signalId", signal.Id)
                            .Add("dataSourceId", dataSourceId)
                            .AddCodes(CurationContentAudit.FieldsKey, CurationContentAudit.GoldenFields))
                        .Digesting(CurationContentAudit.AfterKey, CurationContentAudit.Of(evalCase))
                ],
                cancellationToken);

            await context.McpQuerySignals
                .Where(x => x.Id == request.SignalId)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.PromotedEvalCaseId, evalCase.Id), cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return (int?)evalCase.Id;
        });

        if (created != null)
        {
            return new PromoteSignalToGoldenResult(created.Value);
        }

        // Claimed nothing: another promotion won, or the signal changed since it was reviewed.
        var current = await SignalAsync(context, request.SignalId, cancellationToken);
        if (current != null && (current.PromotedAt != null || current.FirstCaseId != null))
        {
            return AlreadyPromoted(current);
        }

        throw new BeaconConflictException(ChangedSinceReviewedMessage);
    }

    // The case the signal was promoted to, else the first case promoted from it (before promotions were recorded).
    private PromoteSignalToGoldenResult AlreadyPromoted(PromotableSignal signal)
    {
        var caseId = signal.PromotedEvalCaseId ?? signal.FirstCaseId;
        if (caseId == null)
        {
            // Both are written in the claim's transaction, so a claimed signal without its case was changed another way.
            logger.LogWarning("Query signal {SignalId} is marked promoted, but no golden case is recorded for it", signal.Id);

            throw new BeaconConflictException($"Query signal {signal.Id} is marked promoted, but its golden case is missing.");
        }

        return new PromoteSignalToGoldenResult(caseId.Value) { AlreadyPromoted = true };
    }

    // One read: what the promotion decides on, and the first case promoted from the signal.
    private static Task<PromotableSignal?> SignalAsync(BeaconContext context, int signalId, CancellationToken cancellationToken)
    {
        return context.McpQuerySignals
            .Where(x => x.Id == signalId)
            .Select(x =>
                new PromotableSignal
                {
                    Id = x.Id,
                    ProjectId = x.ProjectId,
                    DataSourceId = x.DataSourceId,
                    Tool = x.Tool,
                    Question = x.Question,
                    Verdict = x.UserVerdict,
                    Sql = x.UserCorrectedSql ?? x.CorrectedSql ?? x.GeneratedSql,
                    SqlHash = x.CandidateSqlHash,
                    PromotedAt = x.PromotedAt,
                    PromotedEvalCaseId = x.PromotedEvalCaseId,
                    FirstCaseId = context.McpEvalCases
                        .Where(y => y.SourceSignalId == x.Id)
                        .OrderBy(y => y.Id)
                        .Select(y => (int?)y.Id)
                        .FirstOrDefault()
                })
            .FirstOrDefaultAsync(cancellationToken);
    }

    private sealed class PromotableSignal
    {
        public int Id { get; init; }
        public int? ProjectId { get; init; }
        public int? DataSourceId { get; init; }
        public string Tool { get; init; } = "";
        public string Question { get; init; } = "";
        public McpUserVerdict Verdict { get; init; }
        public string? Sql { get; init; }
        public string? SqlHash { get; init; }
        public DateTime? PromotedAt { get; init; }
        public int? PromotedEvalCaseId { get; init; }
        public int? FirstCaseId { get; init; }
    }
}

/// <param name="SignalId">The candidate to promote.</param>
/// <param name="ReviewedSqlHash">
/// The <see cref="GoldenCandidateItem.SqlHash"/> of the candidate as the Admin reviewed it; a signal whose SQL hashes
/// otherwise now is refused (409).
/// </param>
/// <param name="Notes">Notes stored on the golden case.</param>
[ProjectScopeExempt("admin-gated curation on its route (the eval harness); the golden case's data source must be linked to its project", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record PromoteSignalToGoldenCommand(int SignalId, string ReviewedSqlHash, string? Notes = null) : IRequest<PromoteSignalToGoldenResult>;

public record PromoteSignalToGoldenResult(int EvalCaseId)
{
    /// <summary>True when the signal had already been promoted: <see cref="EvalCaseId"/> is that case, and nothing was created.</summary>
    public bool AlreadyPromoted { get; init; }
}
