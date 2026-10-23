using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Helpers;

namespace Beacon.Core.Services.Retention;

/// <summary>The jobs that leave a <see cref="MaintenanceHeartbeat"/>.</summary>
public static class MaintenanceJobNames
{
    public const string McpAuditRetention = "mcp-audit-retention";
    public const string SecurityAuditRetention = "security-audit-retention";
    public const string SecurityAuditVerify = "security-audit-verify";
}

/// <summary>Records that a maintenance job ran to its end. Never throws: a heartbeat must not fail the job it reports.</summary>
public interface IMaintenanceHeartbeat
{
    Task RecordAsync(string name, CancellationToken cancellationToken);
}

internal sealed class MaintenanceHeartbeatRecorder(
    IDbContextFactory<BeaconContext> contextFactory,
    TimeProvider timeProvider,
    ILogger<MaintenanceHeartbeatRecorder> logger) : IMaintenanceHeartbeat
{
    public async Task RecordAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            var now = timeProvider.GetUtcNow().UtcDateTime;
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            var updated = await context.MaintenanceHeartbeats
                .Where(x => x.Name == name)
                .ExecuteUpdateAsync(x => x.SetProperty(y => y.LastRunTime, now), cancellationToken);
            if (updated > 0)
            {
                return;
            }

            context.MaintenanceHeartbeats.Add(new MaintenanceHeartbeat { Name = name, LastRunTime = now });
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // A second instance may have inserted the row first (unique name): its heartbeat is as good as this one.
            logger.LogWarning("Could not record the heartbeat of the maintenance job {Job} ({Failure})", name, FailureCodes.Describe(ex));
        }
    }
}

/// <summary>What <see cref="IRetentionScheduleCheck"/> found for one job that should be running.</summary>
public sealed record RetentionScheduleFinding(string Job, DateTime? LastRunTime, TimeSpan ExpectedWithin);

/// <summary>
/// Finds the retention and verification jobs that are configured to matter but have not run within their window: the
/// purges the host sets a retention for, and the security audit verification. Beacon schedules none of them itself (the
/// sample host does, with Warp), so a host that never did would otherwise see nothing.
/// </summary>
public interface IRetentionScheduleCheck
{
    Task<IReadOnlyList<RetentionScheduleFinding>> CheckAsync(CancellationToken cancellationToken);
}

internal sealed class RetentionScheduleCheck(
    IDbContextFactory<BeaconContext> contextFactory,
    IOptions<McpDeploymentOptions> mcpOptions,
    IOptions<SecurityAuditOptions> securityAuditOptions,
    TimeProvider timeProvider) : IRetentionScheduleCheck
{
    // The jobs run daily and hourly; a window of a day and a half, and of six hours, tolerates a missed run or two.
    internal static readonly TimeSpan DailyWindow = TimeSpan.FromHours(36);
    internal static readonly TimeSpan HourlyWindow = TimeSpan.FromHours(6);

    public async Task<IReadOnlyList<RetentionScheduleFinding>> CheckAsync(CancellationToken cancellationToken)
    {
        var expected = ExpectedJobs(mcpOptions.Value.Audit?.RetentionDays, securityAuditOptions.Value.RetentionDays);
        var names = expected
            .Select(x => x.Job)
            .ToList();
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var lastRuns = await context.MaintenanceHeartbeats
            .AsNoTracking()
            .Where(x => names.Contains(x.Name))
            .Select(x =>
                new
                {
                    x.Name,
                    x.LastRunTime
                })
            .ToListAsync(cancellationToken);

        return Findings(
            expected,
            lastRuns.ToDictionary(x => x.Name, x => x.LastRunTime, StringComparer.Ordinal),
            timeProvider.GetUtcNow().UtcDateTime);
    }

    /// <summary>The jobs that should be running: a purge only while its retention is set, the verification always.</summary>
    internal static List<(string Job, TimeSpan Window)> ExpectedJobs(int? mcpAuditRetentionDays, int? securityAuditRetentionDays)
    {
        var expected = new List<(string Job, TimeSpan Window)>();
        if (mcpAuditRetentionDays != null)
        {
            expected.Add((MaintenanceJobNames.McpAuditRetention, DailyWindow));
        }

        if (securityAuditRetentionDays != null)
        {
            expected.Add((MaintenanceJobNames.SecurityAuditRetention, DailyWindow));
        }

        expected.Add((MaintenanceJobNames.SecurityAuditVerify, HourlyWindow));

        return expected;
    }

    /// <summary>Each expected job with no recorded run, or none within its window.</summary>
    internal static List<RetentionScheduleFinding> Findings(
        IEnumerable<(string Job, TimeSpan Window)> expected,
        IReadOnlyDictionary<string, DateTime> lastRuns,
        DateTime now)
    {
        var findings = new List<RetentionScheduleFinding>();
        foreach (var (job, window) in expected)
        {
            DateTime? lastRun = lastRuns.TryGetValue(job, out var found) ? found : null;
            if (lastRun == null || now - lastRun.Value > window)
            {
                findings.Add(new RetentionScheduleFinding(job, lastRun, window));
            }
        }

        return findings;
    }
}
