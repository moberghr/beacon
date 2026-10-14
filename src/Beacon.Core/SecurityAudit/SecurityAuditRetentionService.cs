using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Helpers;
using Beacon.Core.Telemetry;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// Deletes security audit events older than <c>Beacon:Audit:RetentionDays</c>. Unset (the default), nothing is deleted:
/// events are kept forever. Before it deletes, the purge verifies every event it is about to delete and refuses when any
/// fails (logged at Error, <see cref="SecurityAuditLogEvents.VerificationFailed"/>, recorded as a refused
/// <see cref="SecurityAuditActions.AuditPurged"/>, and thrown, so the scheduled job fails visibly): tampered evidence is
/// never deleted unseen. The deletion and its <see cref="SecurityAuditActions.AuditPurged"/> event, by the system, commit
/// together in one transaction (and <see cref="SecurityAuditLogEvents.Purged"/> goes to the log stream). A purge that does
/// not run is logged at Error (<see cref="SecurityAuditLogEvents.PurgeFailed"/>), counted, and rethrown. The sample host
/// schedules it as the Warp recurring job <c>security-audit-retention</c>; other hosts schedule it themselves.
/// </summary>
public interface ISecurityAuditRetentionService
{
    /// <summary>Deletes the expired events and returns how many; 0 when no retention is configured.</summary>
    Task<int> PurgeExpiredAsync(CancellationToken cancellationToken);
}

internal sealed class SecurityAuditRetentionService(
    IDbContextFactory<BeaconContext> contextFactory,
    IOptions<SecurityAuditOptions> options,
    ISecurityAuditRecorder recorder,
    SecurityAuditIntegrity integrity,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory) : ISecurityAuditRetentionService
{
    internal const int BatchSize = 1000;
    internal const int MaxReportedEvents = 100;

    private readonly ILogger _auditLogger = loggerFactory.CreateLogger(BeaconTelemetry.AuditLogCategory);

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken)
    {
        var retentionDays = options.Value.RetentionDays;
        if (retentionDays is null)
        {
            _auditLogger.LogDebug("Security audit retention purge skipped: events are kept forever.");

            return 0;
        }

        var now = timeProvider.GetUtcNow();
        var cutoff = CutoffFor(now, retentionDays.Value);
        Purge purge;
        try
        {
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);

            // The rows about to go are verified in the transaction that deletes them; none goes while one fails.
            purge = await recorder.SaveWithEventsAsync(
                context,
                async () =>
                {
                    var failed = await FailedExpiredAsync(context, cutoff, now, retentionDays.Value, cancellationToken);
                    if (failed.Count > 0)
                    {
                        return new Purge(0, failed);
                    }

                    return new Purge(await Expired(context.SecurityAuditEvents, cutoff).ExecuteDeleteAsync(cancellationToken), []);
                },
                x => x.Failed.Count > 0 ? [] : [Purged(x.Deleted, retentionDays.Value, cutoff, SecurityAuditOutcomes.Success, null, null)],
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _auditLogger.LogError(
                SecurityAuditLogEvents.PurgeFailed,
                "Security audit purge of events older than {Cutoff} did not run ({Failure})",
                cutoff,
                FailureCodes.Describe(ex));
            BeaconTelemetry.SecurityAuditWriteFailures.Add(1, new KeyValuePair<string, object?>(BeaconTelemetry.SecurityAuditActionTag, SecurityAuditActions.AuditPurged));

            throw;
        }

        if (purge.Failed.Count > 0)
        {
            await RefuseAsync(purge.Failed, retentionDays.Value, cutoff, cancellationToken);
        }

        _auditLogger.Log(
            LogLevel.Information,
            SecurityAuditLogEvents.Purged,
            "Security audit purge deleted {Deleted} events older than {Cutoff} (retention {RetentionDays} days)",
            purge.Deleted,
            cutoff,
            retentionDays.Value);

        return purge.Deleted;
    }

    /// <summary>Events created strictly before this UTC instant are expired.</summary>
    internal static DateTime CutoffFor(DateTimeOffset now, int retentionDays) =>
        now.UtcDateTime.AddDays(-retentionDays);

    /// <summary>The one predicate the purge runs; shared with the translation tests on both providers (§4.3-§4.5).</summary>
    internal static IQueryable<SecurityAuditEvent> Expired(IQueryable<SecurityAuditEvent> source, DateTime cutoff) =>
        source.Where(x => x.CreatedTime < cutoff);

    // The expired events that do not verify (changed, under an unknown key, or dated in the future), by id.
    private async Task<List<(int Id, string Verdict)>> FailedExpiredAsync(
        BeaconContext context,
        DateTime cutoff,
        DateTimeOffset now,
        int retentionDays,
        CancellationToken cancellationToken)
    {
        // Older than the retention is what the purge is for: only the upper end of the plausible range applies here.
        var range = (DateTime.MinValue, SecurityAuditVerification.PlausibleRange(now, retentionDays).Latest);
        var failed = new List<(int Id, string Verdict)>();
        var lastId = 0;
        while (true)
        {
            var batch = await Expired(context.SecurityAuditEvents.AsNoTracking(), cutoff)
                .Where(x => x.Id > lastId)
                .OrderBy(x => x.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                return failed;
            }

            foreach (var auditEvent in batch)
            {
                var verdict = SecurityAuditVerification.VerdictOf(auditEvent, range, integrity);
                if (verdict != SecurityAuditVerdicts.Intact)
                {
                    failed.Add((auditEvent.Id, verdict));
                }
            }

            lastId = batch[^1].Id;
        }
    }

    private async Task RefuseAsync(List<(int Id, string Verdict)> failed, int retentionDays, DateTime cutoff, CancellationToken cancellationToken)
    {
        var failedIds = failed
            .Select(x => x.Id)
            .Take(MaxReportedEvents)
            .ToList();
        _auditLogger.LogError(
            SecurityAuditLogEvents.VerificationFailed,
            "Security audit purge refused: {FailedCount} of the events older than {Cutoff} fail verification (modified {ModifiedCount}, unknown key {UnknownKeyCount}); first ids {FailedEventIds}. Nothing was deleted",
            failed.Count,
            cutoff,
            failed.Count(x => x.Verdict == SecurityAuditVerdicts.Modified),
            failed.Count(x => x.Verdict == SecurityAuditVerdicts.UnknownKey),
            string.Join(",", failedIds));
        BeaconTelemetry.SecurityAuditVerificationFailures.Add(
            failed.Count,
            new KeyValuePair<string, object?>(BeaconTelemetry.SecurityAuditActionTag, SecurityAuditActions.AuditPurged));

        await recorder.RecordAsync(Purged(0, retentionDays, cutoff, SecurityAuditOutcomes.Refused, failed.Count, failedIds), cancellationToken);

        throw new InvalidOperationException(
            $"The security audit retention purge was refused: {failed.Count} expired events fail verification. Nothing was deleted; investigate the events the log names before purging.");
    }

    private static SecurityAuditEntry Purged(int deleted, int retentionDays, DateTime cutoff, string outcome, int? failedCount, List<int>? failedIds)
    {
        return new SecurityAuditEntry(SecurityAuditActions.AuditPurged)
            .On(SecurityAuditResources.SecurityAudit, null)
            .Ending(outcome)
            .By(SecurityAuditActor.System)
            .With(new SecurityAuditDetails()
                .Add("deletedCount", deleted)
                .Add("retentionDays", retentionDays)
                .Add("cutoff", cutoff)
                .Add("failedCount", failedCount)
                .Add("failedEventIds", failedIds));
    }

    private sealed record Purge(int Deleted, List<(int Id, string Verdict)> Failed);
}
