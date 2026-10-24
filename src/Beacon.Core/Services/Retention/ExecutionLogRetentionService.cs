using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Telemetry;

namespace Beacon.Core.Services.Retention;

/// <summary>
/// Deployment-level purge of the execution log (<c>ManualQueryExecutionLog</c> rows), driven by
/// <c>Beacon:Audit:ExecutionLog:RetentionDays</c>. A <c>null</c> window (the default) is a no-op: rows are kept forever
/// until an operator opts in. Scheduled by the host as the Warp recurring job <c>execution-log-retention</c>
/// (see <c>Beacon.SampleProject/Warp/Jobs/McpMaintenanceJobs.cs</c>); consumer hosts schedule it themselves.
/// </summary>
public interface IExecutionLogRetentionService
{
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken);
}

internal sealed class ExecutionLogRetentionService(
    IDbContextFactory<BeaconContext> contextFactory,
    IOptions<SecurityAuditOptions> options,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory) : IExecutionLogRetentionService
{
    internal static readonly EventId ExecutionLogPurgedEvent = new(9111, "ExecutionLogPurged");

    private readonly ILogger _auditLogger = loggerFactory.CreateLogger(BeaconTelemetry.AuditLogCategory);

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var retentionDays = options.Value.ExecutionLog.RetentionDays;
        if (retentionDays is null)
        {
            _auditLogger.LogDebug("Execution log retention purge skipped: no retention window configured.");

            return 0;
        }

        var cutoff = McpAuditRetentionService.CutoffFor(timeProvider.GetUtcNow(), retentionDays.Value);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var deleted = await Expired(context.ManualQueryExecutionLogs, cutoff).ExecuteDeleteAsync(cancellationToken);

        _auditLogger.Log(
            LogLevel.Information,
            ExecutionLogPurgedEvent,
            "Execution log purge deleted {Deleted} rows older than {Cutoff}",
            deleted,
            cutoff);

        return deleted;
    }

    /// <summary>The one predicate the purge runs, shared with its translation test.</summary>
    internal static IQueryable<ManualQueryExecutionLog> Expired(IQueryable<ManualQueryExecutionLog> source, DateTime cutoff) =>
        source.Where(x => x.CreatedTime < cutoff);
}
