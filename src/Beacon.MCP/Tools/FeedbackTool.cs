using System.ComponentModel;
using System.Diagnostics;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
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
    McpAuditService auditService,
    IMediator mediator,
    IProjectAccessEvaluator projectAccess,
    ILogger<FeedbackTool> logger)
{
    [McpServerTool(Name = "feedback", Title = "Record Answer Feedback", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Record whether a previous `ask` answer was correct. Pass the signal_id from that ask response. A 'correct' verdict is saved as a verified example that improves future answers. Only include a note when the user explicitly asked to record one.")]
    public async Task<CallToolResult> ExecuteAsync(
        [Description("The signal_id from the ask response you are rating")] int signal_id,
        [Description("'correct' or 'incorrect'")] string verdict,
        [Description("Optional: the corrected SQL, if you fixed it")] string? corrected_sql = null,
        [Description("Optional: a short note — only when the user explicitly requested it; never a summary of the conversation.")] string? note = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();

        // §1.11 — audit parameters carry IDENTIFIERS ONLY. corrected_sql / note are user-supplied and may
        // contain PII; the command may persist them, but they must NEVER reach audit parameters or logs.
        var auditParameters = $"signal_id={signal_id};verdict={verdict}";

        // The audit row is attributed to the project of the signal being rated (the transport path carries no
        // active project); null when the signal doesn't exist.
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var projectId = await context.McpQuerySignals
            .Where(x => x.Id == signal_id)
            .Select(x => x.ProjectId)
            .FirstOrDefaultAsync(cancellationToken);

        // A signal is rated only in its own project: it must have one, that project must be the pinned one (or one the
        // caller's project list allows when none is pinned), and the caller needs Query there. Every refusal reads as
        // "not found", so a caller cannot probe other projects' signals, and is attributed to the pinned project.
        if (!await IsRateableAsync(projectId, cancellationToken))
        {
            sw.Stop();
            var notFound = $"Query signal {signal_id} not found.";
            await auditService.LogToolCallAsync(null, projectContext.UserId, "feedback",
                auditParameters, null, projectContext.ActiveProjectId, (int)sw.ElapsedMilliseconds, null, notFound, ct: cancellationToken);
            return ToolHelper.Error(notFound);
        }

        if (!TryParseVerdict(verdict, out var parsedVerdict))
        {
            sw.Stop();
            var validationError = "verdict must be 'correct' or 'incorrect'.";
            await auditService.LogToolCallAsync(null, projectContext.UserId, "feedback",
                auditParameters, null, projectId, (int)sw.ElapsedMilliseconds, null, validationError, ct: cancellationToken);
            return ToolHelper.Error(validationError);
        }

        try
        {
            await mediator.Send(new RecordQueryFeedbackCommand(signal_id, parsedVerdict, corrected_sql, note), cancellationToken);
            sw.Stop();
            await auditService.LogToolCallAsync(null, projectContext.UserId, "feedback",
                auditParameters, null, projectId, (int)sw.ElapsedMilliseconds, null, null, ct: cancellationToken);
            return ToolHelper.Success($"Feedback recorded for signal {signal_id}.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or BeaconException)
        {
            sw.Stop();
            // §1.11 — log identifiers only; corrected_sql / note never reach the logger. ex.Message here is the
            // handler's "signal not found" or refusal text (safe), passed to the audit error slot only.
            logger.LogWarning("Feedback recording failed for signal {SignalId}", signal_id);
            await auditService.LogToolCallAsync(null, projectContext.UserId, "feedback",
                auditParameters, null, projectId, (int)sw.ElapsedMilliseconds, null, ex.Message, ct: CancellationToken.None);
            return ToolHelper.Error(ex.Message);
        }
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

        // A null list is a caller with no project list (no request); an empty list allows nothing.
        if (projectContext.AllowedProjectIds != null && !projectContext.AllowedProjectIds.Contains(signalProjectId.Value))
        {
            return false;
        }

        var scope = await projectAccess.GetScopeAsync(cancellationToken);

        return scope.Allows(signalProjectId.Value, ProjectAction.Query);
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
