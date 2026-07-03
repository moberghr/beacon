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

    public async Task<McpSession?> GetOrCreateSessionAsync(string sessionId, int? userId, int? apiKeyId, CancellationToken ct = default)
    {
        await using var context = await contextFactory.CreateDbContextAsync(ct);
        var session = await context.McpSessions.FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);

        if (session == null)
        {
            session = new McpSession
            {
                SessionId = sessionId,
                UserId = userId,
                ApiKeyId = apiKeyId
            };
            context.McpSessions.Add(session);
            await context.SaveChangesAsync(ct);
        }

        return session;
    }

    public async Task UpdateSessionActivityAsync(string sessionId, CancellationToken ct = default)
    {
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(ct);
            var session = await context.McpSessions.FirstOrDefaultAsync(s => s.SessionId == sessionId, ct);
            if (session != null)
            {
                session.LastActivityAt = DateTime.UtcNow;
                session.QueriesExecuted++;
                await context.SaveChangesAsync(ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to update MCP session activity for session {SessionId}", sessionId);
        }
    }
}
