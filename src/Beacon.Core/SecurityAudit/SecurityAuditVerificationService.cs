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
/// scheduled <see cref="SecurityAuditActions.AuditVerified"/> event (by the system) names, and checks every newer event,
/// in id order and never selected by its time (the time is a field the HMAC covers), against its HMAC and then the range a
/// genuine event can be dated in. The checkpoint advances only as far as every lower id has been checked: an id with no
/// event below a stored one (a transaction that has not committed yet) is waited for, and the events above it are checked
/// again by the next run, until the nearest intact event above it is <see cref="GapGrace"/> old; then the id is taken as
/// never committed (an insert that rolled back consumes its identity value), counted in the checkpoint, and passed.
/// Events that fail are logged at Error (<see cref="SecurityAuditLogEvents.VerificationFailed"/>, 9108) and counted; every
/// run writes a checkpoint to the log stream (<see cref="SecurityAuditLogEvents.Checkpoint"/>, 9109: how many it checked,
/// the checkpoint id and that event's HMAC, the ids passed as never committed), which anchors the trail outside the
/// database; a run that checked events records them as a system <see cref="SecurityAuditActions.AuditVerified"/> event.
/// Each run then verifies the MCP audit rows written since its last run (<see cref="IMcpAuditVerificationService"/>).
/// The sample host schedules it hourly as the Warp recurring job <c>security-audit-verify</c>; other hosts schedule it
/// themselves.
/// </summary>
public interface ISecurityAuditVerificationService
{
    /// <summary>Verifies the events written since the last scheduled verification.</summary>
    Task<SecurityAuditCheckpoint> VerifyNewEventsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What one scheduled verification covered: events after <see cref="FromEventId"/>, every id up to
/// <see cref="MaxEventId"/> checked or passed as never committed (its HMAC <see cref="LastHmac"/>), how many it checked and
/// how many failed.
/// </summary>
public sealed record SecurityAuditCheckpoint(
    int FromEventId,
    int MaxEventId,
    string? LastHmac,
    int CheckedCount,
    int ModifiedCount,
    int UnknownKeyCount,
    int OutOfRangeCount,
    IReadOnlyList<int> FailedEventIds)
{
    /// <summary>How many ids up to <see cref="MaxEventId"/> had no event and were passed as never committed.</summary>
    public int NeverCommittedCount { get; init; }

    /// <summary>The first id passed as never committed; null when none was.</summary>
    public int? FirstNeverCommittedId { get; init; }

    /// <summary>The last id passed as never committed; null when none was.</summary>
    public int? LastNeverCommittedId { get; init; }

    /// <summary>
    /// The id above <see cref="MaxEventId"/> with no event that is still waited for (it may not have committed yet); the
    /// events above it were checked and are checked again by the next run. Null when nothing is waited for.
    /// </summary>
    public int? WaitingForEventId { get; init; }
}

internal sealed class SecurityAuditVerificationService(
    IDbContextFactory<BeaconContext> contextFactory,
    SecurityAuditIntegrity integrity,
    ISecurityAuditRecorder recorder,
    SecurityAuditReadGate gate,
    IOptions<SecurityAuditOptions> options,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory,
    IMcpAuditVerificationService mcpAuditVerification) : ISecurityAuditVerificationService
{
    internal const int BatchSize = 1000;

    /// <summary>The most events one run checks; a backlog is worked off over the following runs.</summary>
    internal const int MaxEventsPerRun = 50_000;

    internal const int MaxReportedEvents = 100;

    /// <summary>
    /// How long an id with no event below a stored one is waited for before it is passed as never committed, counted from
    /// the time of the nearest intact event above it (written after that id was handed out).
    /// </summary>
    internal static readonly TimeSpan GapGrace = TimeSpan.FromHours(1);

    /// <summary>The detail a scheduled verification records its checkpoint id under.</summary>
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
            await mcpAuditVerification.VerifyNewRowsAsync(cancellationToken);

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

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var fromEventId = await HighWaterMarkAsync(context, cancellationToken);
        var lastId = fromEventId;
        var seen = new List<SeenEvent>();
        var failed = new List<int>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (seen.Count < MaxEventsPerRun)
        {
            // By id alone: an event is never passed over for the time it carries.
            var batch = await context.SecurityAuditEvents
                .AsNoTracking()
                .Where(x => x.Id > lastId)
                .OrderBy(x => x.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var auditEvent in batch)
            {
                var verdict = SecurityAuditVerification.SealFirstVerdictOf(auditEvent, range, integrity);
                counts[verdict] = counts.GetValueOrDefault(verdict) + 1;
                if (verdict != SecurityAuditVerdicts.Intact)
                {
                    failed.Add(auditEvent.Id);
                }

                seen.Add(new SeenEvent(auditEvent.Id, auditEvent.Hmac, verdict == SecurityAuditVerdicts.Intact ? auditEvent.CreatedTime : null));
            }

            lastId = batch[^1].Id;
        }

        var advanced = Advance(fromEventId, seen, now.UtcDateTime - GapGrace);
        var outOfRangeCount = counts.GetValueOrDefault(SecurityAuditVerdicts.OutOfRange);

        return new SecurityAuditCheckpoint(
            fromEventId,
            advanced.MaxEventId,
            advanced.LastHmac,
            seen.Count - outOfRangeCount,
            counts.GetValueOrDefault(SecurityAuditVerdicts.Modified),
            counts.GetValueOrDefault(SecurityAuditVerdicts.UnknownKey),
            outOfRangeCount,
            failed
                .Take(MaxReportedEvents)
                .ToList())
        {
            NeverCommittedCount = advanced.NeverCommittedCount,
            FirstNeverCommittedId = advanced.FirstNeverCommittedId,
            LastNeverCommittedId = advanced.LastNeverCommittedId,
            WaitingForEventId = advanced.WaitingForEventId
        };
    }

    // The highest id below which every id was checked by this run (or an earlier one), or passed as never committed. An id
    // with no event below a checked one is passed only once the nearest intact event above it, written after that id was
    // handed out, is older than passGapsBefore; until then the checkpoint stays below it and the next run checks again.
    internal static Advanced Advance(int fromEventId, List<SeenEvent> seen, DateTime passGapsBefore)
    {
        var intactAbove = new DateTime?[seen.Count];
        DateTime? nearest = null;
        for (var i = seen.Count - 1; i >= 0; i--)
        {
            nearest = seen[i].IntactTime ?? nearest;
            intactAbove[i] = nearest;
        }

        var maxEventId = fromEventId;
        string? lastHmac = null;
        var neverCommitted = 0;
        int? firstNeverCommitted = null;
        int? lastNeverCommitted = null;
        for (var i = 0; i < seen.Count; i++)
        {
            var missingFrom = maxEventId + 1;
            if (seen[i].Id > missingFrom)
            {
                if (intactAbove[i] is not { } handedOutBefore || handedOutBefore > passGapsBefore)
                {
                    return new Advanced(maxEventId, lastHmac, neverCommitted, firstNeverCommitted, lastNeverCommitted, missingFrom);
                }

                neverCommitted += seen[i].Id - missingFrom;
                firstNeverCommitted ??= missingFrom;
                lastNeverCommitted = seen[i].Id - 1;
            }

            maxEventId = seen[i].Id;
            lastHmac = seen[i].Hmac;
        }

        return new Advanced(maxEventId, lastHmac, neverCommitted, firstNeverCommitted, lastNeverCommitted, null);
    }

    // The checkpoint id the latest intact scheduled verification recorded; 0 when there is none. A checkpoint event that
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
            "Security audit checkpoint: FromEventId={FromEventId} MaxEventId={MaxEventId} Checked={Checked} Failed={Failed} LastHmac={LastHmac} KeyId={KeyId} NeverCommitted={NeverCommittedCount} FirstNeverCommittedId={FirstNeverCommittedId} LastNeverCommittedId={LastNeverCommittedId} WaitingForEventId={WaitingForEventId}",
            checkpoint.FromEventId,
            checkpoint.MaxEventId,
            checkpoint.CheckedCount,
            failedCount,
            checkpoint.LastHmac,
            integrity.CurrentKeyId,
            checkpoint.NeverCommittedCount,
            checkpoint.FirstNeverCommittedId,
            checkpoint.LastNeverCommittedId,
            checkpoint.WaitingForEventId);

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
                    .Add("failedEventIds", checkpoint.FailedEventIds)
                    .Add("neverCommittedCount", checkpoint.NeverCommittedCount)
                    .Add("firstNeverCommittedId", checkpoint.FirstNeverCommittedId)
                    .Add("lastNeverCommittedId", checkpoint.LastNeverCommittedId)
                    .Add("waitingForEventId", checkpoint.WaitingForEventId)),
            cancellationToken);
    }

    // An event this run read: its id and HMAC, and its time when it verified intact (only then is the time its own).
    internal readonly record struct SeenEvent(int Id, string Hmac, DateTime? IntactTime);

    internal sealed record Advanced(
        int MaxEventId,
        string? LastHmac,
        int NeverCommittedCount,
        int? FirstNeverCommittedId,
        int? LastNeverCommittedId,
        int? WaitingForEventId);
}
