using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;

namespace Beacon.MCP.Services;

internal sealed class McpAuditService(
    IDbContextFactory<BeaconContext> contextFactory,
    ILogger<McpAuditService> logger)
{
    public async Task LogToolCallAsync(int? sessionId, int? userId, string tool, string? parameters,
        int? dataSourceId, int? projectId, int executionTimeMs, int? resultRowCount, string? errorMessage, CancellationToken ct = default)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            context.McpAuditLogs.Add(new McpAuditLog
            {
                SessionId = sessionId,
                UserId = userId,
                Tool = tool,
                Parameters = parameters?.Length > 4000 ? parameters[..4000] : parameters,
                DataSourceId = dataSourceId,
                ProjectId = projectId,
                ExecutionTimeMs = executionTimeMs,
                ResultRowCount = resultRowCount,
                ErrorMessage = errorMessage?.Length > 4000 ? errorMessage[..4000] : errorMessage
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

}
