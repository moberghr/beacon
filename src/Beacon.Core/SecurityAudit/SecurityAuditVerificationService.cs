using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Services.Retention;
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
/// Each run also checks again up to <see cref="SecurityAuditVerificationService.RecheckEventsPerRun"/> events an earlier run
/// checked, by their HMAC, continuing where the last run's re-check stopped and starting over once it reaches the last
/// checkpoint, so an event changed after it was checked is found within one full cycle. It counts the events up to its
/// checkpoint that the retention purge cannot delete before the next run (all of them without a retention; else those dated
/// no earlier than the purge's cutoff plus <see cref="SecurityAuditVerificationService.CountMargin"/>), and the events above
/// its checkpoint that it read (those it checked while waiting for a lower id), with the newest id it read; the next run
/// compares: fewer events up to the checkpoint is an event deleted (<c>events_missing</c>), more is one written below it
/// (<c>events_added</c>), fewer events above it or the newest one gone is an event deleted from the end of the trail
/// (<c>tail_events_missing</c>). An id that never held an event is not counted, so it stays informational.
/// Events that fail are logged at Error (<see cref="SecurityAuditLogEvents.VerificationFailed"/>, 9108) and counted; every
/// run writes a checkpoint to the log stream (<see cref="SecurityAuditLogEvents.Checkpoint"/>, 9109: how many it checked,
/// the checkpoint id and that event's HMAC, the ids passed as never committed), which anchors the trail outside the
/// database; a run that checked events records them as a system <see cref="SecurityAuditActions.AuditVerified"/> event.
/// Each run then verifies the MCP audit rows written since its last run (<see cref="IMcpAuditVerificationService"/>), and
/// the rows of the other sealed audit tables, the execution log and the settings history (<see cref="IAuditTableVerificationService"/>).
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
/// how many failed, the events it checked again, and how the stored events compare with what the last run counted.
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

    /// <summary>How many events an earlier run checked this run checked again, by their HMAC; their failures are counted above.</summary>
    public int RecheckedCount { get; init; }

    /// <summary>Where the next run's re-check continues; 0 when this run's re-check reached the last checkpoint.</summary>
    public int RecheckedThroughEventId { get; init; }

    /// <summary>Events up to the last checkpoint that it counted and that are no longer stored.</summary>
    public int MissingEventCount { get; init; }

    /// <summary>Events up to the last checkpoint that it did not count and that are stored now.</summary>
    public int AddedEventCount { get; init; }

    /// <summary>Events above the last checkpoint that it read and counted and that are no longer stored.</summary>
    public int MissingTailEventCount { get; init; }

    /// <summary>The earliest time an event is counted from (the purge cannot reach it before the next run); null without a retention.</summary>
    public DateTime? RetainedFrom { get; init; }

    /// <summary>The events up to <see cref="MaxEventId"/> dated no earlier than <see cref="RetainedFrom"/>; null while there is no checkpoint.</summary>
    public int? RetainedEventCount { get; init; }

    /// <summary>The highest event id this run read; above <see cref="MaxEventId"/> while a lower id is waited for.</summary>
    public int NewestEventId { get; init; }

    /// <summary>The events above <see cref="MaxEventId"/> up to <see cref="NewestEventId"/> dated no earlier than <see cref="RetainedFrom"/>.</summary>
    public int RetainedTailCount { get; init; }

    /// <summary>Every failure this run found.</summary>
    public int FailedCount => ModifiedCount + UnknownKeyCount + OutOfRangeCount + MissingEventCount + AddedEventCount + MissingTailEventCount;

    /// <summary>Why this run failed, as codes (<see cref="SecurityAuditVerificationReasons"/>); empty when it did not.</summary>
    public IReadOnlyList<string> FailureReasons =>
        new (int Count, string Reason)[]
            {
                (ModifiedCount, SecurityAuditVerificationReasons.Modified),
                (UnknownKeyCount, SecurityAuditVerificationReasons.UnknownKey),
                (OutOfRangeCount, SecurityAuditVerificationReasons.OutOfRange),
                (MissingEventCount, SecurityAuditVerificationReasons.EventsMissing),
                (AddedEventCount, SecurityAuditVerificationReasons.EventsAdded),
                (MissingTailEventCount, SecurityAuditVerificationReasons.TailEventsMissing)
            }
            .Where(x => x.Count > 0)
            .Select(x => x.Reason)
            .ToList();
}

/// <summary>Why a scheduled security audit verification failed, as its event's <c>failureReasons</c> records it.</summary>
public static class SecurityAuditVerificationReasons
{
    /// <summary>An event's fields do not match its HMAC (found when first checked, or checked again).</summary>
    public const string Modified = SecurityAuditVerdicts.Modified;

    /// <summary>An event names a key this deployment does not hold.</summary>
    public const string UnknownKey = SecurityAuditVerdicts.UnknownKey;

    /// <summary>An event that verifies is dated where no genuine event can be.</summary>
    public const string OutOfRange = SecurityAuditVerdicts.OutOfRange;

    /// <summary>Fewer events up to the last checkpoint than it counted: an event that was stored is gone.</summary>
    public const string EventsMissing = "events_missing";

    /// <summary>More events up to the last checkpoint than it counted: an event was written below it.</summary>
    public const string EventsAdded = "events_added";

    /// <summary>Fewer events above the last checkpoint than it read and counted, or the newest one it read is gone.</summary>
    public const string TailEventsMissing = "tail_events_missing";
}

internal sealed class SecurityAuditVerificationService(
    IDbContextFactory<BeaconContext> contextFactory,
    SecurityAuditIntegrity integrity,
    ISecurityAuditRecorder recorder,
    SecurityAuditReadGate gate,
    IOptions<SecurityAuditOptions> options,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory,
    IMcpAuditVerificationService mcpAuditVerification,
    IAuditTableVerificationService auditTableVerification,
    IMaintenanceHeartbeat? heartbeat = null) : ISecurityAuditVerificationService
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

    /// <summary>The most events one run checks again; a full cycle takes as many runs as it needs.</summary>
    internal const int RecheckEventsPerRun = 10_000;

    /// <summary>
    /// How far past the purge's cutoff an event must be dated to be counted: the next run compares the count only while the
    /// purge cannot have reached a counted event (a daily purge that runs late, plus a margin).
    /// </summary>
    internal static readonly TimeSpan CountMargin = TimeSpan.FromDays(2);

    /// <summary>The detail a scheduled verification records its checkpoint id under.</summary>
    internal const string MaxEventIdDetail = "maxEventId";

    internal const string RecheckedThroughEventIdDetail = "recheckedThroughEventId";
    internal const string RetainedFromDetail = "retainedFrom";
    internal const string RetainedEventCountDetail = "retainedEventCount";
    internal const string NewestEventIdDetail = "newestEventId";
    internal const string RetainedTailCountDetail = "retainedTailCount";

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
            await auditTableVerification.VerifyNewRowsAsync(cancellationToken);

            if (heartbeat != null)
            {
                await heartbeat.RecordAsync(MaintenanceJobNames.SecurityAuditVerify, cancellationToken);
            }

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
        var retentionDays = options.Value.RetentionDays;
        var range = SecurityAuditVerification.PlausibleRange(now, retentionDays);

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var last = await LastCheckpointAsync(context, cancellationToken);
        var fromEventId = last.MaxEventId;
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
        var checkedCount = seen.Count - outOfRangeCount;

        // Events earlier runs checked, again, by their HMAC only (their time was judged when first checked): an event
        // changed after it was checked is found within one full cycle.
        var rechecked = 0;
        var recheckedThrough = last.RecheckedThrough < last.MaxEventId ? last.RecheckedThrough : 0;
        while (last.MaxEventId > 0 && rechecked < RecheckEventsPerRun)
        {
            var batch = await context.SecurityAuditEvents
                .AsNoTracking()
                .Where(x => x.Id > recheckedThrough)
                .Where(x => x.Id <= last.MaxEventId)
                .OrderBy(x => x.Id)
                .Take(Math.Min(BatchSize, RecheckEventsPerRun - rechecked))
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var auditEvent in batch)
            {
                var verdict = SealVerdictOf(auditEvent);
                if (verdict != SecurityAuditVerdicts.Intact)
                {
                    counts[verdict] = counts.GetValueOrDefault(verdict) + 1;
                    failed.Add(auditEvent.Id);
                }
            }

            rechecked += batch.Count;
            recheckedThrough = batch[^1].Id;
        }

        var compared = await CompareCountsAsync(context, last, now.UtcDateTime, retentionDays, cancellationToken);
        var retainedFrom = retentionDays == null ? (DateTime?)null : McpAuditVerificationService.WholeSeconds(now.UtcDateTime.AddDays(-retentionDays.Value) + CountMargin);
        var newestEventId = seen.Count > 0 ? seen[^1].Id : advanced.MaxEventId;
        int? retainedEventCount = advanced.MaxEventId == 0
            ? null
            : await Counted(context.SecurityAuditEvents, 0, advanced.MaxEventId, retainedFrom).CountAsync(cancellationToken);
        var retainedTailCount = newestEventId > advanced.MaxEventId
            ? await Counted(context.SecurityAuditEvents, advanced.MaxEventId, newestEventId, retainedFrom).CountAsync(cancellationToken)
            : 0;

        return new SecurityAuditCheckpoint(
            fromEventId,
            advanced.MaxEventId,
            advanced.LastHmac,
            checkedCount,
            counts.GetValueOrDefault(SecurityAuditVerdicts.Modified),
            counts.GetValueOrDefault(SecurityAuditVerdicts.UnknownKey),
            outOfRangeCount,
            failed
                .Distinct()
                .Take(MaxReportedEvents)
                .ToList())
        {
            NeverCommittedCount = advanced.NeverCommittedCount,
            FirstNeverCommittedId = advanced.FirstNeverCommittedId,
            LastNeverCommittedId = advanced.LastNeverCommittedId,
            WaitingForEventId = advanced.WaitingForEventId,
            RecheckedCount = rechecked,
            RecheckedThroughEventId = rechecked < RecheckEventsPerRun ? 0 : recheckedThrough,
            MissingEventCount = compared.Missing,
            AddedEventCount = compared.Added,
            MissingTailEventCount = compared.MissingTail,
            RetainedFrom = retainedFrom,
            RetainedEventCount = retainedEventCount,
            NewestEventId = newestEventId,
            RetainedTailCount = retainedTailCount
        };
    }

    /// <summary>
    /// The events with an id above <paramref name="afterId"/> up to <paramref name="throughId"/> that a count covers: those
    /// dated no earlier than <paramref name="retainedFrom"/>, all of them without one. Shared with the translation tests on
    /// both providers (§4.3-§4.5).
    /// </summary>
    internal static IQueryable<SecurityAuditEvent> Counted(IQueryable<SecurityAuditEvent> source, int afterId, int throughId, DateTime? retainedFrom)
    {
        var counted = source
            .Where(x => x.Id > afterId)
            .Where(x => x.Id <= throughId);
        if (retainedFrom is { } from)
        {
            counted = counted.Where(x => x.CreatedTime >= from);
        }

        return counted;
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

    // The events up to the last checkpoint that it counted, against those stored now, and the events above it that it read
    // and counted, against those still there. Compared only while the purge cannot have reached a counted event since: no
    // retention now, or a run within the margin of the last one. An id that never held an event was never counted.
    private async Task<CountComparison> CompareCountsAsync(
        BeaconContext context,
        LastCheckpoint last,
        DateTime now,
        int? retentionDays,
        CancellationToken cancellationToken)
    {
        if (last.RetainedEventCount is not { } counted || last.MaxEventId == 0)
        {
            return new CountComparison(0, 0, 0);
        }

        var comparable = retentionDays == null
            || (last.RetainedFrom is { } retainedFrom && now.AddDays(-retentionDays.Value) + SecurityAuditVerification.ClockSkew <= retainedFrom);
        if (!comparable)
        {
            _auditLogger.LogWarning(
                SecurityAuditLogEvents.Checkpoint,
                "Security audit events up to event {MaxEventId} were not counted against the last checkpoint: it is older than the margin, or the retention was set since",
                last.MaxEventId);

            return new CountComparison(0, 0, 0);
        }

        var present = await Counted(context.SecurityAuditEvents, 0, last.MaxEventId, last.RetainedFrom).CountAsync(cancellationToken);
        var missingTail = 0;
        if (last.NewestEventId > last.MaxEventId && last.RetainedTailCount is { } tailCounted)
        {
            // An id the last run waited for may have committed since: more events here is no failure, fewer is.
            var presentTail = await Counted(context.SecurityAuditEvents, last.MaxEventId, last.NewestEventId, last.RetainedFrom).CountAsync(cancellationToken);
            var newestStored = await context.SecurityAuditEvents
                .Where(x => x.Id == last.NewestEventId)
                .AnyAsync(cancellationToken);
            missingTail = Math.Max(Math.Max(0, tailCounted - presentTail), newestStored ? 0 : 1);
        }

        return new CountComparison(Math.Max(0, counted - present), Math.Max(0, present - counted), missingTail);
    }

    // The checkpoint the latest intact scheduled verification recorded: the id it reached, where its re-check stopped, and
    // what it counted; none when there is no such event. A checkpoint event that does not verify is itself reported and
    // passed over: a forged one cannot make a run skip events.
    private async Task<LastCheckpoint> LastCheckpointAsync(BeaconContext context, CancellationToken cancellationToken)
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

            var details = string.IsNullOrEmpty(auditEvent.DetailsJson) ? null : JsonNode.Parse(auditEvent.DetailsJson);
            if (McpAuditVerificationService.IntOf(details, MaxEventIdDetail) is not { } maxEventId)
            {
                continue;
            }

            return new LastCheckpoint(
                maxEventId,
                McpAuditVerificationService.IntOf(details, RecheckedThroughEventIdDetail) ?? 0,
                McpAuditVerificationService.IntOf(details, RetainedEventCountDetail),
                McpAuditVerificationService.TimeOf(details, RetainedFromDetail),
                McpAuditVerificationService.IntOf(details, NewestEventIdDetail) ?? maxEventId,
                McpAuditVerificationService.IntOf(details, RetainedTailCountDetail));
        }

        return new LastCheckpoint(0, 0, null, null, 0, null);
    }

    // The verdict on an event by its HMAC alone.
    private string SealVerdictOf(SecurityAuditEvent auditEvent)
    {
        return integrity.Verify(auditEvent) switch
        {
            SecurityAuditVerdict.Intact => SecurityAuditVerdicts.Intact,
            SecurityAuditVerdict.UnknownKey => SecurityAuditVerdicts.UnknownKey,
            _ => SecurityAuditVerdicts.Modified
        };
    }

    private async Task ReportAsync(SecurityAuditCheckpoint checkpoint, CancellationToken cancellationToken)
    {
        var failedCount = checkpoint.FailedCount;
        if (failedCount > 0)
        {
            _auditLogger.LogError(
                SecurityAuditLogEvents.VerificationFailed,
                "Security audit verification found {FailedCount} failures ({FailureReasons}: modified {ModifiedCount}, unknown key {UnknownKeyCount}, out of range {OutOfRangeCount}, events missing {MissingEventCount}, events added {AddedEventCount}, newest events missing {MissingTailEventCount}) after event {FromEventId} and in {RecheckedCount} events checked again; first ids {FailedEventIds}",
                failedCount,
                string.Join(",", checkpoint.FailureReasons),
                checkpoint.ModifiedCount,
                checkpoint.UnknownKeyCount,
                checkpoint.OutOfRangeCount,
                checkpoint.MissingEventCount,
                checkpoint.AddedEventCount,
                checkpoint.MissingTailEventCount,
                checkpoint.FromEventId,
                checkpoint.RecheckedCount,
                string.Join(",", checkpoint.FailedEventIds));
            BeaconTelemetry.SecurityAuditVerificationFailures.Add(
                failedCount,
                new KeyValuePair<string, object?>(BeaconTelemetry.SecurityAuditActionTag, SecurityAuditActions.AuditVerified));
        }

        _auditLogger.Log(
            LogLevel.Information,
            SecurityAuditLogEvents.Checkpoint,
            "Security audit checkpoint: FromEventId={FromEventId} MaxEventId={MaxEventId} Checked={Checked} Rechecked={Rechecked} RecheckedThroughEventId={RecheckedThroughEventId} Failed={Failed} LastHmac={LastHmac} KeyId={KeyId} NeverCommitted={NeverCommittedCount} FirstNeverCommittedId={FirstNeverCommittedId} LastNeverCommittedId={LastNeverCommittedId} WaitingForEventId={WaitingForEventId} RetainedEventCount={RetainedEventCount} NewestEventId={NewestEventId} RetainedTailCount={RetainedTailCount}",
            checkpoint.FromEventId,
            checkpoint.MaxEventId,
            checkpoint.CheckedCount,
            checkpoint.RecheckedCount,
            checkpoint.RecheckedThroughEventId,
            failedCount,
            checkpoint.LastHmac,
            integrity.CurrentKeyId,
            checkpoint.NeverCommittedCount,
            checkpoint.FirstNeverCommittedId,
            checkpoint.LastNeverCommittedId,
            checkpoint.WaitingForEventId,
            checkpoint.RetainedEventCount,
            checkpoint.NewestEventId,
            checkpoint.RetainedTailCount);

        // A run that read no new event and found nothing records nothing: the next run compares against the same checkpoint.
        if (checkpoint.CheckedCount + checkpoint.OutOfRangeCount + failedCount == 0)
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
                    .Add("waitingForEventId", checkpoint.WaitingForEventId)
                    .Add("recheckedCount", checkpoint.RecheckedCount)
                    .Add(RecheckedThroughEventIdDetail, checkpoint.RecheckedThroughEventId)
                    .Add("missingEventCount", checkpoint.MissingEventCount)
                    .Add("addedEventCount", checkpoint.AddedEventCount)
                    .Add("missingTailEventCount", checkpoint.MissingTailEventCount)
                    .Add(RetainedFromDetail, checkpoint.RetainedFrom)
                    .Add(RetainedEventCountDetail, checkpoint.RetainedEventCount)
                    .Add(NewestEventIdDetail, checkpoint.NewestEventId)
                    .Add(RetainedTailCountDetail, checkpoint.RetainedTailCount)
                    .AddCodes("failureReasons", checkpoint.FailureReasons)),
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

    private sealed record LastCheckpoint(
        int MaxEventId,
        int RecheckedThrough,
        int? RetainedEventCount,
        DateTime? RetainedFrom,
        int NewestEventId,
        int? RetainedTailCount);

    private sealed record CountComparison(int Missing, int Added, int MissingTail);
}
