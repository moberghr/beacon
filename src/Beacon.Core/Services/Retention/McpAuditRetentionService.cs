using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Telemetry;

namespace Beacon.Core.Services.Retention;

/// <summary>
/// Deployment-level purge of <c>McpAuditLog</c> rows, driven by <c>Beacon:Mcp:Audit:RetentionDays</c>. A
/// <c>null</c> window (the default) is a no-op — audit rows are kept forever until an operator opts in.
/// Scheduled by the host as the Warp recurring job <c>mcp-audit-retention</c>
/// (see <c>Beacon.SampleProject/Warp/Jobs/McpMaintenanceJobs.cs</c>); consumer hosts schedule it themselves.
/// </summary>
public interface IMcpAuditRetentionService
{
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken);
}

internal sealed class McpAuditRetentionService(
    IDbContextFactory<BeaconContext> contextFactory,
    IOptions<McpDeploymentOptions> deploymentOptions,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory) : IMcpAuditRetentionService
{
    internal static readonly EventId AuditPurgedEvent = new(9102, "McpAuditPurged");

    private readonly ILogger _auditLogger = loggerFactory.CreateLogger(BeaconTelemetry.AuditLogCategory);

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var retentionDays = deploymentOptions.Value.Audit?.RetentionDays;
        if (retentionDays is null)
        {
            _auditLogger.LogDebug("MCP audit retention purge skipped: no retention window configured.");

            return 0;
        }

        var cutoff = CutoffFor(timeProvider.GetUtcNow(), retentionDays.Value);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var deleted = await Expired(context.McpAuditLogs, cutoff).ExecuteDeleteAsync(cancellationToken);

        _auditLogger.Log(
            LogLevel.Information,
            AuditPurgedEvent,
            "MCP audit purge deleted {Deleted} rows older than {Cutoff}",
            deleted,
            cutoff);

        return deleted;
    }

    /// <summary>Rows created strictly before this UTC instant are expired.</summary>
    internal static DateTime CutoffFor(DateTimeOffset now, int retentionDays) =>
        now.UtcDateTime.AddDays(-retentionDays);

    /// <summary>
    /// The one predicate the purge runs. Shared with <c>McpAuditRetentionTests</c> so the translation test
    /// exercises exactly what production runs (§4.3-§4.5).
    /// </summary>
    internal static IQueryable<McpAuditLog> Expired(IQueryable<McpAuditLog> source, DateTime cutoff) =>
        source.Where(x => x.CreatedTime < cutoff);
}
