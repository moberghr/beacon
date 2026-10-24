using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Authorization;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Handlers.McpEval;

/// <summary>
/// Edits a golden eval case — its question, gold SQL, active flag, or notes — and records the editing user
/// (<see cref="Data.Entities.McpEvalCase.ModifiedByUserId"/>). Only the fields supplied on the command are applied; the
/// rest are left untouched. New gold SQL is stored as <see cref="IGroundingSqlCheck"/> returns it: checked in the case's
/// data source's dialect, without its comments. Throws (§9.8) when the case does not exist, the new gold SQL does not pass
/// the check, or the new question holds characters a reviewer cannot see (<see cref="GroundingText"/>) — nothing is changed
/// then; refuses (403) a caller without a Beacon user, since who edited is recorded.
/// </summary>
internal sealed class UpdateGoldenCaseHandler(
    IDbContextFactory<BeaconContext> contextFactory,
    IGroundingSqlCheck groundingSqlCheck,
    IBeaconActorAccessor actorAccessor,
    ISecurityAuditRecorder securityAudit,
    ILogger<UpdateGoldenCaseHandler> logger)
    : IRequestHandler<UpdateGoldenCaseCommand>
{
    internal const string UnidentifiedEditorMessage = "Editing a golden case needs a signed-in Beacon user: who edited it is recorded.";

    public async Task Handle(UpdateGoldenCaseCommand request, CancellationToken cancellationToken)
    {
        if (request.Question?.Length > InputLimits.GoldenQuestionMaxLength)
        {
            throw new BeaconValidationException($"The question is longer than {InputLimits.GoldenQuestionMaxLength} characters.");
        }

        if (request.GoldSql?.Length > InputLimits.GoldenSqlMaxLength)
        {
            throw new BeaconValidationException($"The gold SQL is longer than {InputLimits.GoldenSqlMaxLength} characters.");
        }

        if (request.Notes?.Length > InputLimits.GoldenNotesMaxLength)
        {
            throw new BeaconValidationException($"The notes are longer than {InputLimits.GoldenNotesMaxLength} characters.");
        }

        var actor = await actorAccessor.GetCurrentAsync(cancellationToken);
        if (actor.BeaconUserId == null)
        {
            throw AccessRefusal.Of(logger, actor, "golden case", request.Id, UnidentifiedEditorMessage);
        }

        if (GroundingText.HasHiddenCharacters(request.Question))
        {
            throw new InvalidOperationException("The question was not saved: it holds characters that do not show on screen. Remove them and try again.");
        }

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

        var evalCase = await context.McpEvalCases
            .Where(x => x.Id == request.Id)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException($"Eval case {request.Id} not found.");

        // Checked before anything changes: an edit whose gold SQL is refused changes nothing.
        var goldSql = request.GoldSql == null
            ? null
            : await CheckedGoldSqlAsync(evalCase.DataSourceId, request.GoldSql, cancellationToken);

        var activeBefore = evalCase.IsActive;
        var questionChanged = request.Question != null && !string.Equals(request.Question, evalCase.Question, StringComparison.Ordinal);
        var goldSqlChanged = goldSql != null && !string.Equals(goldSql, evalCase.GoldSql, StringComparison.Ordinal);
        var notesChanged = request.Notes != null && !string.Equals(request.Notes, evalCase.Notes, StringComparison.Ordinal);
        if (request.Question != null)
        {
            evalCase.Question = request.Question;
        }

        if (goldSql != null)
        {
            evalCase.GoldSql = goldSql;
            // The frozen gold fingerprint no longer matches the edited SQL — clear it so the next run
            // re-derives it from a fresh gold execution.
            evalCase.GoldResultFingerprint = null;
        }

        if (request.IsActive.HasValue)
        {
            evalCase.IsActive = request.IsActive.Value;
        }

        if (request.Notes != null)
        {
            evalCase.Notes = request.Notes;
        }

        evalCase.ModifiedByUserId = actor.BeaconUserId;
        await securityAudit.StageAsync(
            context,
            new SecurityAuditEntry(SecurityAuditActions.GoldenCaseEdited)
                .On(SecurityAuditResources.EvalCase, evalCase.Id)
                .InProject(evalCase.ProjectId)
                .With(new SecurityAuditDetails()
                    .Add("questionChanged", questionChanged)
                    .Add("goldSqlChanged", goldSqlChanged)
                    .Add("notesChanged", notesChanged)
                    .Add("isActiveBefore", activeBefore)
                    .Add("isActiveAfter", evalCase.IsActive)),
            cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<string> CheckedGoldSqlAsync(int dataSourceId, string goldSql, CancellationToken cancellationToken)
    {
        var check = await groundingSqlCheck.CheckAsync(dataSourceId, goldSql, cancellationToken);
        if (!check.IsAllowed)
        {
            throw new InvalidOperationException($"The gold SQL was not saved: {check.Refusal}");
        }

        return check.Sql;
    }
}

[ProjectScopeExempt("admin-gated curation (the eval harness)", Policy = ProjectScopeExemptAttribute.AdminPolicy)]
public record UpdateGoldenCaseCommand : IRequest
{
    public required int Id { get; init; }
    public string? Question { get; init; }
    public string? GoldSql { get; init; }
    public bool? IsActive { get; init; }
    public string? Notes { get; init; }
}
