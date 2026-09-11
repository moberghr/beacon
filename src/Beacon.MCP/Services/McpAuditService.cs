using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Services;
using Beacon.Core.Services.Retention;

namespace Beacon.MCP.Services;

internal sealed class McpAuditService(
    IDbContextFactory<BeaconContext> contextFactory,
    IMcpSettingsProvider settingsProvider,
    ILogger<McpAuditService> logger)
{
    public async Task LogToolCallAsync(int? sessionId, int? userId, string tool, string? parameters,
        int? dataSourceId, int? projectId, int executionTimeMs, int? resultRowCount, string? errorMessage,
        IReadOnlyList<string>? tables = null, CancellationToken ct = default)
    {
        try
        {
            // §1.7 — the audit row is written whatever happens here. A settings/cache failure must not lose the
            // row, so it resolves in its own try and fails CLOSED (structural form) rather than aborting the write.
            var retainContent = await RetainsContentAsync(projectId, ct);

            string? loggedParameters;
            string? loggedErrorMessage;
            if (!retainContent)
            {
                loggedParameters = McpContentRedactor.StructuralAuditParameters(tool, parameters, tables);
                loggedErrorMessage = McpContentRedactor.ErrorClassOf(errorMessage);
            }
            else
            {
                loggedParameters = parameters?.Length > 4000 ? parameters[..4000] : parameters;
                loggedErrorMessage = errorMessage?.Length > 4000 ? errorMessage[..4000] : errorMessage;
            }

            await using var context = await contextFactory.CreateDbContextAsync(ct);
            context.McpAuditLogs.Add(new McpAuditLog
            {
                SessionId = sessionId,
                UserId = userId,
                Tool = tool,
                Parameters = loggedParameters,
                DataSourceId = dataSourceId,
                ProjectId = projectId,
                ExecutionTimeMs = executionTimeMs,
                ResultRowCount = resultRowCount,
                ErrorMessage = loggedErrorMessage
            });
            await context.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // §1.7 — audit logging is non-optional. Swallow so a transient DB issue doesn't fail the
            // tool call, but log at Error so a sustained audit-sink outage is operationally visible.
            logger.LogError(ex, "Failed to log MCP audit entry for tool {Tool}", tool);
        }
    }

    private async Task<bool> RetainsContentAsync(int? projectId, CancellationToken ct)
    {
        try
        {
            var settings = await settingsProvider.GetEffectiveSettingsAsync(projectId ?? 0, ct);

            return ContentRetentionDecision.From(settings).RetainQueryContent;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Content retention settings unavailable for project {ProjectId}; auditing structurally.", projectId);

            return false;
        }
    }

}
