using System.ComponentModel;
using System.Diagnostics;
using MediatR;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.McpEval;
using Beacon.MCP.Services;

namespace Beacon.MCP.Tools;

[McpServerToolType]
internal sealed class FeedbackTool(
    IProjectContext projectContext,
    McpProjectContextManager sessionManager,
    McpAuditService auditService,
    IMediator mediator,
    ILogger<FeedbackTool> logger)
{
    [McpServerTool(Name = "feedback")]
    [Description("Record whether a previous `ask` answer was correct. Pass the signal_id from that ask response. A 'correct' verdict is saved as a verified example that improves future answers.")]
    public async Task<CallToolResult> ExecuteAsync(
        [Description("The signal_id from the ask response you are rating")] int signal_id,
        [Description("'correct' or 'incorrect'")] string verdict,
        [Description("Optional: the corrected SQL, if you fixed it")] string? corrected_sql = null,
        [Description("Optional: a short note")] string? note = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        // §1.11 — audit parameters carry IDENTIFIERS ONLY. corrected_sql / note are user-supplied and may
        // contain PII; the command may persist them, but they must NEVER reach audit parameters or logs.
        var auditParameters = $"signal_id={signal_id};verdict={verdict}";

        // The active project (if any) is recorded for the audit trail; feedback itself is not project-scoped,
        // so we never gate on project resolution here.
        var projectId = ResolveActiveProjectId();

        if (!TryParseVerdict(verdict, out var parsedVerdict))
        {
            sw.Stop();
            var validationError = "verdict must be 'correct' or 'incorrect'.";
            await auditService.LogToolCallAsync(null, projectContext.UserId, "feedback",
                auditParameters, null, projectId, (int)sw.ElapsedMilliseconds, null, validationError, cancellationToken);
            return ToolHelper.Error(validationError);
        }

        try
        {
            await mediator.Send(new RecordQueryFeedbackCommand(signal_id, parsedVerdict, corrected_sql, note), cancellationToken);
            sw.Stop();
            await auditService.LogToolCallAsync(null, projectContext.UserId, "feedback",
                auditParameters, null, projectId, (int)sw.ElapsedMilliseconds, null, null, cancellationToken);
            return ToolHelper.Success($"Feedback recorded for signal {signal_id}.");
        }
        catch (InvalidOperationException ex)
        {
            sw.Stop();
            // §1.11 — log identifiers only; corrected_sql / note never reach the logger. ex.Message here is the
            // handler's "signal not found" text (safe), passed to the audit error slot only.
            logger.LogWarning("Feedback recording failed for signal {SignalId}", signal_id);
            await auditService.LogToolCallAsync(null, projectContext.UserId, "feedback",
                auditParameters, null, projectId, (int)sw.ElapsedMilliseconds, null, ex.Message, CancellationToken.None);
            return ToolHelper.Error(ex.Message);
        }
    }

    private int? ResolveActiveProjectId()
    {
        var key = McpProjectContextManager.MakeKey(projectContext.UserId, projectContext.ApiKeyId);
        var state = sessionManager.GetOrCreate(key);
        return projectContext.ActiveProjectId ?? state.ActiveProjectId;
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
