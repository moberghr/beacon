using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Telemetry;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// Verifies the security audit events written since the last scheduled verification, so a changed or forged event is
/// found within the schedule's period without anyone asking. It continues from the highest event id the last intact
/// scheduled <see cref="SecurityAuditActions.AuditVerified"/> event (by the system) names, and checks each newer event,
/// once it is a few minutes old, against its HMAC and the range a genuine event can be dated in. Events that fail are
/// logged at Error (<see cref="SecurityAuditLogEvents.VerificationFailed"/>, 9108) and counted; every run writes a
/// checkpoint to the log stream (<see cref="SecurityAuditLogEvents.Checkpoint"/>, 9109: how many it checked, the highest id
/// and that event's HMAC), which anchors the trail outside the database; a run that checked events records them as a
/// system <see cref="SecurityAuditActions.AuditVerified"/> event. The sample host schedules it hourly as the Warp recurring
/// job <c>security-audit-verify</c>; other hosts schedule it themselves.
/// </summary>
public interface ISecurityAuditVerificationService
{
    /// <summary>Verifies the events written since the last scheduled verification.</summary>
    Task<SecurityAuditCheckpoint> VerifyNewEventsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What one scheduled verification covered: events after <see cref="FromEventId"/> up to <see cref="MaxEventId"/> (its
/// HMAC <see cref="LastHmac"/>), how many it checked and how many failed.
/// </summary>
public sealed record SecurityAuditCheckpoint(
    int FromEventId,
    int MaxEventId,
    string? LastHmac,
    int CheckedCount,
    int ModifiedCount,
    int UnknownKeyCount,
    int OutOfRangeCount,
    IReadOnlyList<int> FailedEventIds);

internal sealed class SecurityAuditVerificationService(
    IDbContextFactory<BeaconContext> contextFactory,
    SecurityAuditIntegrity integrity,
    ISecurityAuditRecorder recorder,
    SecurityAuditReadGate gate,
    IOptions<SecurityAuditOptions> options,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory) : ISecurityAuditVerificationService
{
    internal const int BatchSize = 1000;

    /// <summary>The most events one run checks; a backlog is worked off over the following runs.</summary>
    internal const int MaxEventsPerRun = 50_000;

    internal const int MaxReportedEvents = 100;

    /// <summary>How old an event must be before a run checks it, so one whose transaction has not committed yet is not passed over.</summary>
    internal static readonly TimeSpan Settle = TimeSpan.FromMinutes(5);

    /// <summary>The detail a scheduled verification records its highest checked event id under.</summary>
    internal const string MaxEventIdDetail = "maxEventId";

    private const int RecentCheckpointsRead = 20;

    private readonly ILogger _auditLogger = loggerFactory.CreateLogger(BeaconTelemetry.AuditLogCategory);

    public async Task<SecurityAuditCheckpoint> VerifyNewEventsAsync(CancellationToken cancellationToken)
    {
        await gate.StartVerificationAsync(cancellationToken);
        try
        {
            var checkpoint = await VerifyAsync(cancellationToken);
            await ReportAsync(checkpoint, cancellationToken);

            return checkpoint;
        }
        finally
        {
            gate.EndVerification();
        }
    }

    private async Task<SecurityAuditCheckpoint> VerifyAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var range = SecurityAuditVerification.PlausibleRange(now, options.Value.RetentionDays);
        var settledBefore = now.UtcDateTime - Settle;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var fromEventId = await HighWaterMarkAsync(context, cancellationToken);
        var lastId = fromEventId;
        string? lastHmac = null;
        var checkedCount = 0;
        var outOfRangeCount = 0;
        var failed = new List<int>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (checkedCount + outOfRangeCount < MaxEventsPerRun)
        {
            var batch = await context.SecurityAuditEvents
                .AsNoTracking()
                .Where(x => x.Id > lastId)
                .Where(x => x.CreatedTime <= settledBefore)
                .OrderBy(x => x.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var auditEvent in batch)
            {
                var verdict = SecurityAuditVerification.VerdictOf(auditEvent, range, integrity);
                if (verdict == SecurityAuditVerdicts.OutOfRange)
                {
                    outOfRangeCount++;
                }
                else
                {
                    checkedCount++;
                    counts[verdict] = counts.GetValueOrDefault(verdict) + 1;
                }

                if (verdict != SecurityAuditVerdicts.Intact)
                {
                    failed.Add(auditEvent.Id);
                }
            }

            lastId = batch[^1].Id;
            lastHmac = batch[^1].Hmac;
        }

        return new SecurityAuditCheckpoint(
            fromEventId,
            lastId,
            lastHmac,
            checkedCount,
            counts.GetValueOrDefault(SecurityAuditVerdicts.Modified),
            counts.GetValueOrDefault(SecurityAuditVerdicts.UnknownKey),
            outOfRangeCount,
            failed
                .Take(MaxReportedEvents)
                .ToList());
    }

    // The highest event id the latest intact scheduled verification checked; 0 when there is none. A checkpoint event that
    // does not verify is itself reported and passed over: a forged one cannot make a run skip events.
    private async Task<int> HighWaterMarkAsync(BeaconContext context, CancellationToken cancellationToken)
    {
        var recent = await context.SecurityAuditEvents
            .AsNoTracking()
            .Where(x => x.Action == SecurityAuditActions.AuditVerified)
            .Where(x => x.ActorKind == SecurityAuditActorKinds.System)
            .Where(x => x.ActorUserId == null)
            .OrderByDescending(x => x.Id)
            .Take(RecentCheckpointsRead)
            .ToListAsync(cancellationToken);
        foreach (var auditEvent in recent)
        {
            if (integrity.Verify(auditEvent) != SecurityAuditVerdict.Intact)
            {
                _auditLogger.LogError(
                    SecurityAuditLogEvents.VerificationFailed,
                    "Security audit verification checkpoint {AuditId} fails verification and is passed over",
                    auditEvent.Id);
                BeaconTelemetry.SecurityAuditVerificationFailures.Add(1, new KeyValuePair<string, object?>(BeaconTelemetry.SecurityAuditActionTag, SecurityAuditActions.AuditVerified));

                continue;
            }

            if (MaxEventIdOf(auditEvent.DetailsJson) is { } maxEventId)
            {
                return maxEventId;
            }
        }

        return 0;
    }

    private static int? MaxEventIdOf(string? detailsJson)
    {
        if (string.IsNullOrEmpty(detailsJson))
        {
            return null;
        }

        return JsonNode.Parse(detailsJson)?[MaxEventIdDetail] is JsonValue value && value.TryGetValue<int>(out var maxEventId)
            ? maxEventId
            : null;
    }

    private async Task ReportAsync(SecurityAuditCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var failedCount = checkpoint.ModifiedCount + checkpoint.UnknownKeyCount + checkpoint.OutOfRangeCount;
        if (failedCount > 0)
        {
            _auditLogger.LogError(
                SecurityAuditLogEvents.VerificationFailed,
                "Security audit verification found {FailedCount} events that fail (modified {ModifiedCount}, unknown key {UnknownKeyCount}, out of range {OutOfRangeCount}) after event {FromEventId}; first ids {FailedEventIds}",
                failedCount,
                checkpoint.ModifiedCount,
                checkpoint.UnknownKeyCount,
                checkpoint.OutOfRangeCount,
                checkpoint.FromEventId,
                string.Join(",", checkpoint.FailedEventIds));
            BeaconTelemetry.SecurityAuditVerificationFailures.Add(
                failedCount,
                new KeyValuePair<string, object?>(BeaconTelemetry.SecurityAuditActionTag, SecurityAuditActions.AuditVerified));
        }

        _auditLogger.Log(
            LogLevel.Information,
            SecurityAuditLogEvents.Checkpoint,
            "Security audit checkpoint: FromEventId={FromEventId} MaxEventId={MaxEventId} Checked={Checked} Failed={Failed} LastHmac={LastHmac} KeyId={KeyId}",
            checkpoint.FromEventId,
            checkpoint.MaxEventId,
            checkpoint.CheckedCount,
            failedCount,
            checkpoint.LastHmac,
            integrity.CurrentKeyId);

        if (checkpoint.CheckedCount + checkpoint.OutOfRangeCount == 0)
        {
            return;
        }

        await recorder.RecordAsync(
            new SecurityAuditEntry(SecurityAuditActions.AuditVerified)
                .On(SecurityAuditResources.SecurityAudit, null)
                .Ending(failedCount > 0 ? SecurityAuditOutcomes.Failure : SecurityAuditOutcomes.Success)
                .By(SecurityAuditActor.System)
                .With(new SecurityAuditDetails()
                    .Add("scheduled", true)
                    .Add("fromEventId", checkpoint.FromEventId)
                    .Add(MaxEventIdDetail, checkpoint.MaxEventId)
                    .Add("checkedCount", checkpoint.CheckedCount)
                    .Add("modifiedCount", checkpoint.ModifiedCount)
                    .Add("unknownKeyCount", checkpoint.UnknownKeyCount)
                    .Add("outOfRangeCount", checkpoint.OutOfRangeCount)
                    .Add("failedEventIds", checkpoint.FailedEventIds)),
            cancellationToken);
    }
}
