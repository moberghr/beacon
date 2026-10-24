using System.ComponentModel;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpEval;
using Beacon.Core.Models;
using Beacon.MCP.Services;

namespace Beacon.MCP.Tools;

[McpServerToolType]
internal sealed class FeedbackTool(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectContext projectContext,
    McpToolCallRecorder recorder,
    IMediator mediator,
    IProjectAccessEvaluator projectAccess)
{
    private const string CallerCancelledMessage = "Cancelled by the caller.";

    [McpServerTool(Name = "feedback", Title = "Record Answer Feedback", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Record whether a previous `ask` answer was correct. Pass the signal_id from that ask response. An 'incorrect' verdict keeps the answer out of the common-query and join patterns Beacon learns; a 'correct' one lists it for an Admin, who may promote it to a verified example for future answers. Corrected SQL is kept only when it passes Beacon's SQL check; the answer says when it was not kept. Only include a note when the user explicitly asked to record one.")]
    public async Task<CallToolResult> ExecuteAsync(
        [Description("The signal_id from the ask response you are rating")] int signal_id,
        [Description("'correct' or 'incorrect'")] string verdict,
        [Description("Optional: the corrected SQL, if you fixed it")] string? corrected_sql = null,
        [Description("Optional: a short note — only when the user explicitly requested it; never a summary of the conversation.")] string? note = null,
        CancellationToken cancellationToken = default)
    {
        // §1.11 — audit parameters carry IDENTIFIERS ONLY. corrected_sql / note are user-supplied and may
        // contain PII; the command may persist them, but they must NEVER reach audit parameters or logs.
        // Written by McpAuditCallToolFilter after the tool returns or throws (§1.7); audit-only, no signal (§9.5).
        recorder
            .Start("feedback", projectContext.UserId, $"signal_id={signal_id};verdict={verdict}")
            .WithProject(projectContext.ActiveProjectId);

        try
        {
            return await RateAsync(signal_id, verdict, corrected_sql, note, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller's own cancellation stays a cancellation; the call is still audited (§1.7).
            recorder.FailCancelled(CallerCancelledMessage);

            throw;
        }
    }

    private async Task<CallToolResult> RateAsync(
        int signalId,
        string verdict,
        string? correctedSql,
        string? note,
        CancellationToken cancellationToken)
    {
        // The audit row is attributed to the project of the signal being rated (the transport path carries no
        // active project); null when the signal doesn't exist.
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var projectId = await context.McpQuerySignals
            .Where(x => x.Id == signalId)
            .Select(x => x.ProjectId)
            .FirstOrDefaultAsync(cancellationToken);

        // A signal is rated only in its own project: it must have one, that project must be the pinned one (or one the
        // caller's project list allows when none is pinned), and the caller needs Query there. Every refusal reads as
        // "not found", so a caller cannot probe other projects' signals, and is attributed to the pinned project.
        if (!await IsRateableAsync(projectId, cancellationToken))
        {
            var notFound = $"Query signal {signalId} not found.";
            recorder.Fail(notFound);
            return ToolHelper.Error(notFound);
        }

        recorder.WithProject(projectId);

        if (!TryParseVerdict(verdict, out var parsedVerdict))
        {
            var validationError = "verdict must be 'correct' or 'incorrect'.";
            recorder.Fail(validationError);
            return ToolHelper.Error(validationError);
        }

        // A refusal the handler throws (signal not found, the SQL check's reason) is recorded by the audit filter, which
        // answers its message as the error, as the query tool answers a gate refusal.
        var result = await mediator.Send(new RecordQueryFeedbackCommand(signalId, parsedVerdict, correctedSql, note), cancellationToken);
        recorder.Succeed();
        return ToolHelper.Success(Answer(signalId, parsedVerdict, result));
    }

    private async Task<bool> IsRateableAsync(int? signalProjectId, CancellationToken cancellationToken)
    {
        if (signalProjectId == null)
        {
            return false;
        }

        if (projectContext.ActiveProjectId != null && projectContext.ActiveProjectId != signalProjectId)
        {
            return false;
        }

        // Only a project the caller's list holds; a missing list (no caller resolved) or an empty one allows nothing.
        if (projectContext.AllowedProjectIds?.Contains(signalProjectId.Value) != true)
        {
            return false;
        }

        var scope = await projectAccess.GetScopeAsync(cancellationToken);

        return scope.Allows(signalProjectId.Value, ProjectAction.Query);
    }

    // What the verdict does, and whether the corrected SQL was kept (and why not).
    private static string Answer(int signalId, McpUserVerdict verdict, RecordQueryFeedbackResult result)
    {
        var effect = verdict == McpUserVerdict.Correct
            ? "An Admin can promote this answer to a verified example for future answers; until then it does not change them."
            : "Beacon no longer learns common-query or join patterns from this answer.";
        var correctedSql = result.CorrectedSqlNotRecordedReason != null
            ? $" The corrected SQL was not kept: {result.CorrectedSqlNotRecordedReason}"
            : result.CorrectedSqlRecorded
                ? " The corrected SQL was kept with the verdict, without its comments."
                : "";

        return $"Feedback recorded for signal {signalId}. {effect}{correctedSql}";
    }

    private static bool TryParseVerdict(string verdict, out McpUserVerdict parsed)
    {
        if (string.Equals(verdict, "correct", StringComparison.OrdinalIgnoreCase))
        {
            parsed = McpUserVerdict.Correct;
            return true;
        }

        if (string.Equals(verdict, "incorrect", StringComparison.OrdinalIgnoreCase))
        {
            parsed = McpUserVerdict.Incorrect;
            return true;
        }

        parsed = McpUserVerdict.Unset;
        return false;
    }
}
