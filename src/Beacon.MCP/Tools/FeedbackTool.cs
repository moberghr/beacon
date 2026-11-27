using System.ComponentModel;
using System.Globalization;
using MediatR;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpEval;
using Beacon.Core.Models;
using Beacon.Core.Services.Security;
using Beacon.MCP.Services;

namespace Beacon.MCP.Tools;

[McpServerToolType]
internal sealed class FeedbackTool(
    IDbContextFactory<BeaconContext> contextFactory,
    IProjectContext projectContext,
    McpToolCallRecorder recorder,
    IMediator mediator,
    IProjectAccessEvaluator projectAccess,
    IServiceProvider serviceProvider)
{
    private const string CallerCancelledMessage = "Cancelled by the caller.";

    // The audit parameters' code for a reason outside the list: never the value the caller sent (§1.11).
    private const string InvalidReasonCode = "invalid";

    // The reasons a caller may give, by the snake_case code the tool accepts and the audit records.
    private static readonly (string Code, McpFeedbackReason Reason)[] Reasons =
    [
        ("wrong_table", McpFeedbackReason.WrongTable),
        ("wrong_column", McpFeedbackReason.WrongColumn),
        ("wrong_join", McpFeedbackReason.WrongJoin),
        ("wrong_filter", McpFeedbackReason.WrongFilter),
        ("wrong_aggregation", McpFeedbackReason.WrongAggregation),
        ("wrong_time_range", McpFeedbackReason.WrongTimeRange),
        ("misunderstood_question", McpFeedbackReason.MisunderstoodQuestion),
        ("other", McpFeedbackReason.Other)
    ];

    [McpServerTool(Name = "feedback", Title = "Record Answer Feedback", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Record whether a previous `ask` answer was correct. Pass the signal_id from that ask response. An 'incorrect' verdict keeps the answer out of the common-query and join patterns Beacon learns; a 'correct' one lists it for an Admin, who may promote it to a verified example for future answers. Corrected SQL is kept only when it passes Beacon's SQL check; the answer says when it was not kept. Only include a note when the user explicitly asked to record one.")]
    public async Task<CallToolResult> ExecuteAsync(
        [Description("The signal_id from the ask response you are rating")] int signal_id,
        [Description("'correct' or 'incorrect'")] string verdict,
        [Description("Optional: the corrected SQL, if you fixed it")] string? corrected_sql = null,
        [Description("Optional: a short note — only when the user explicitly requested it; never a summary of the conversation.")] string? note = null,
        [Description("Optional, for an 'incorrect' verdict: one of wrong_table, wrong_column, wrong_join, wrong_filter, wrong_aggregation, wrong_time_range, misunderstood_question, other")] string? reason = null,
        CancellationToken cancellationToken = default)
    {
        // §1.11 — audit parameters carry IDENTIFIERS and CODES ONLY. corrected_sql / note / reason are user-supplied and
        // may contain PII; the command may persist corrected_sql and note, but they must NEVER reach audit parameters or
        // logs. The reason reaches them only as its parsed code, or "invalid".
        // Written by McpAuditCallToolFilter after the tool returns or throws (§1.7); audit-only, no signal (§9.5).
        var reasonParsed = TryParseReason(reason, out var parsedReason);
        var reasonCode = reasonParsed ? parsedReason?.Code : InvalidReasonCode;
        recorder
            .Start("feedback", projectContext.UserId, AuditParameters(signal_id, verdict, reasonCode))
            .WithProject(projectContext.ActiveProjectId);

        // Beacon:RateLimits:Feedback, per caller, MCP and playground alike: refused at once, before any lookup, and audited
        // like every other refusal (§1.7). The limiter is registered with the MCP server; without it the application's
        // stand-in applies.
        if (!McpRateLimiter.For(serviceProvider).Feedback.TryAcquire(McpQueryConcurrencyLimiter.CallerKeyOf(projectContext), out var retryAfter))
        {
            var refusal = string.Format(
                CultureInfo.InvariantCulture,
                McpRateLimiter.FeedbackRefusalFormat,
                RateLimitPartitions.RetryAfterSeconds(retryAfter));
            recorder.Fail(refusal);

            return ToolHelper.Error(refusal);
        }

        try
        {
            return await RateAsync(signal_id, verdict, corrected_sql, note, reasonParsed, parsedReason?.Reason, cancellationToken);
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
        bool reasonParsed,
        McpFeedbackReason? reason,
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

        // The allowed codes only, never the value sent.
        if (!reasonParsed)
        {
            var reasonError = $"reason must be one of {string.Join(", ", Reasons.Select(x => x.Code))}.";
            recorder.Fail(reasonError);
            return ToolHelper.Error(reasonError);
        }

        // A refusal the handler throws (signal not found, the SQL check's reason) is recorded by the audit filter, which
        // answers its message as the error, as the query tool answers a gate refusal.
        var result = await mediator.Send(new RecordQueryFeedbackCommand(signalId, parsedVerdict, correctedSql, note, reason), cancellationToken);
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

    // No reason (null, empty or blank) parses as none; otherwise one of the codes, ignoring case and surrounding spaces.
    private static bool TryParseReason(string? reason, out (string Code, McpFeedbackReason Reason)? parsed)
    {
        parsed = null;
        if (string.IsNullOrWhiteSpace(reason))
        {
            return true;
        }

        var trimmed = reason.Trim();
        parsed = Reasons
            .Where(x => string.Equals(x.Code, trimmed, StringComparison.OrdinalIgnoreCase))
            .Select(x => ((string Code, McpFeedbackReason Reason)?)x)
            .FirstOrDefault();

        return parsed != null;
    }

    // Identifiers and codes only (§1.11): the reason as its code, "invalid" or nothing when none was given.
    private static string AuditParameters(int signalId, string verdict, string? reasonCode)
    {
        var parameters = $"signal_id={signalId};verdict={verdict}";

        return reasonCode == null ? parameters : $"{parameters};reason={reasonCode}";
    }
}
