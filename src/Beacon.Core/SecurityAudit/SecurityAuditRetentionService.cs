using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Helpers;
using Beacon.Core.Services.Retention;
using Beacon.Core.Telemetry;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// Deletes security audit events older than <c>Beacon:Audit:RetentionDays</c>. Unset (the default), nothing is deleted:
/// events are kept forever. Before it deletes, the purge verifies every event it is about to delete and refuses when any
/// fails (logged at Error, <see cref="SecurityAuditLogEvents.VerificationFailed"/>, recorded as a refused
/// <see cref="SecurityAuditActions.AuditPurged"/>, and thrown, so the scheduled job fails visibly): tampered evidence is
/// never deleted unseen. It then deletes, batch by batch, only the events it has just verified intact, each by its id and
/// the HMAC it verified, never by their date alone: an event changed after it was verified, or one that became expired
/// meanwhile, is not deleted, and is left (counted in the purge's event) for the next purge, which verifies it first. The
/// deletion and its <see cref="SecurityAuditActions.AuditPurged"/> event, by the system, commit together in one transaction
/// (and <see cref="SecurityAuditLogEvents.Purged"/> goes to the log stream). A purge that does not run is logged at Error
/// (<see cref="SecurityAuditLogEvents.PurgeFailed"/>), counted, and rethrown. The sample host schedules it as the Warp
/// recurring job <c>security-audit-retention</c>; other hosts schedule it themselves.
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
    ILoggerFactory loggerFactory,
    IMaintenanceHeartbeat? heartbeat = null) : ISecurityAuditRetentionService
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

            // The rows about to go are verified in the transaction that deletes them; none goes while one fails, and only
            // the rows verified intact go.
            purge = await recorder.SaveWithEventsAsync(
                context,
                async () =>
                {
                    var failed = await FailedExpiredAsync(context, cutoff, now, retentionDays.Value, cancellationToken);
                    if (failed.Count > 0)
                    {
                        return new Purge(0, 0, 0, failed);
                    }

                    return await DeleteVerifiedAsync(context, cutoff, now, retentionDays.Value, cancellationToken);
                },
                x => x.Failed.Count > 0 ? [] : [Purged(x, retentionDays.Value, cutoff)],
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

        // Fewer deleted than verified: rows changed (or went) after they were verified. Rows left: those, and rows that
        // became expired while the purge ran; the next purge verifies them before it deletes any.
        _auditLogger.Log(
            purge.Left > 0 || purge.Deleted < purge.Verified ? LogLevel.Warning : LogLevel.Information,
            SecurityAuditLogEvents.Purged,
            "Security audit purge deleted {Deleted} of the {Verified} events it verified older than {Cutoff} (retention {RetentionDays} days); {Left} expired events are left for the next purge",
            purge.Deleted,
            purge.Verified,
            cutoff,
            retentionDays.Value,
            purge.Left);

        if (heartbeat != null)
        {
            await heartbeat.RecordAsync(MaintenanceJobNames.SecurityAuditRetention, cancellationToken);
        }

        return purge.Deleted;
    }

    /// <summary>Events created strictly before this UTC instant are expired.</summary>
    internal static DateTime CutoffFor(DateTimeOffset now, int retentionDays) =>
        now.UtcDateTime.AddDays(-retentionDays);

    /// <summary>The expired events the purge reads and counts; shared with the translation tests on both providers (§4.3-§4.5).</summary>
    internal static IQueryable<SecurityAuditEvent> Expired(IQueryable<SecurityAuditEvent> source, DateTime cutoff) =>
        source.Where(x => x.CreatedTime < cutoff);

    /// <summary>
    /// The rows the purge deletes for a batch it verified intact, in id order: each by its id and the HMAC it was verified
    /// with, compared together, within the batch's id range (an index seek); never by date. Shared with the translation
    /// tests on both providers (§4.3-§4.5).
    /// </summary>
    internal static IQueryable<SecurityAuditEvent> Verified(IQueryable<SecurityAuditEvent> source, IReadOnlyList<SecurityAuditEvent> verified)
    {
        var firstId = verified[0].Id;
        var lastId = verified[^1].Id;
        var seals = verified
            .Select(x => SealOf(x.Id, x.Hmac))
            .ToList();

        return source
            .Where(x => x.Id >= firstId)
            .Where(x => x.Id <= lastId)
            .Where(x => seals.Contains(x.Id.ToString() + ":" + x.Hmac));
    }

    // An event's id and HMAC as the delete compares them, as the database writes an id as text.
    private static string SealOf(int id, string hmac)
    {
        return string.Create(CultureInfo.InvariantCulture, $"{id}:{hmac}");
    }

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

    // Reads the expired events again, batch by batch, verifies each and deletes the batch's intact ones by id and HMAC
    // (Verified): an event changed since it was read keeps it, an event that fails now stays. Then counts the expired
    // events still stored: those, and any that became expired while the purge ran, are left for the next purge.
    private async Task<Purge> DeleteVerifiedAsync(
        BeaconContext context,
        DateTime cutoff,
        DateTimeOffset now,
        int retentionDays,
        CancellationToken cancellationToken)
    {
        var range = (DateTime.MinValue, SecurityAuditVerification.PlausibleRange(now, retentionDays).Latest);
        var verified = 0;
        var deleted = 0;
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
                break;
            }

            var intact = batch
                .Where(x => SecurityAuditVerification.VerdictOf(x, range, integrity) == SecurityAuditVerdicts.Intact)
                .ToList();
            if (intact.Count > 0)
            {
                verified += intact.Count;
                deleted += await Verified(context.SecurityAuditEvents, intact).ExecuteDeleteAsync(cancellationToken);
            }

            lastId = batch[^1].Id;
        }

        var left = await Expired(context.SecurityAuditEvents, cutoff).CountAsync(cancellationToken);

        return new Purge(deleted, verified, left, []);
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

        await recorder.RecordAsync(
            Purged(SecurityAuditOutcomes.Refused, retentionDays, cutoff, new SecurityAuditDetails()
                .Add("deletedCount", 0)
                .Add("failedCount", failed.Count)
                .Add("failedEventIds", failedIds)),
            cancellationToken);

        throw new InvalidOperationException(
            $"The security audit retention purge was refused: {failed.Count} expired events fail verification. Nothing was deleted; investigate the events the log names before purging.");
    }

    // A purge that ran: how many it deleted of the events it verified, and the expired events it left for the next purge.
    private static SecurityAuditEntry Purged(Purge purge, int retentionDays, DateTime cutoff)
    {
        return Purged(SecurityAuditOutcomes.Success, retentionDays, cutoff, new SecurityAuditDetails()
            .Add("deletedCount", purge.Deleted)
            .Add("verifiedCount", purge.Verified)
            .Add("leftCount", purge.Left));
    }

    private static SecurityAuditEntry Purged(string outcome, int retentionDays, DateTime cutoff, SecurityAuditDetails details)
    {
        return new SecurityAuditEntry(SecurityAuditActions.AuditPurged)
            .On(SecurityAuditResources.SecurityAudit, null)
            .Ending(outcome)
            .By(SecurityAuditActor.System)
            .With(details
                .Add("retentionDays", retentionDays)
                .Add("cutoff", cutoff));
    }

    private sealed record Purge(int Deleted, int Verified, int Left, List<(int Id, string Verdict)> Failed);
}
