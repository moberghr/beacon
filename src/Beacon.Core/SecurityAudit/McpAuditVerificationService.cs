using System.Globalization;
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
/// Verifies the MCP audit rows against their seals (<see cref="McpAuditSeal"/>), the way the scheduled security audit
/// verification checks its events, and runs as part of it (<see cref="ISecurityAuditVerificationService.VerifyNewEventsAsync"/>),
/// so a host that schedules that job verifies both. Each run:
/// <list type="bullet">
/// <item>continues from the highest row id the last intact <see cref="SecurityAuditActions.McpAuditVerified"/> event by the
/// system names, checks every newer row in id order (never selected by its time), waits for a missing id and passes it as
/// never committed after the grace, as that verification does;</item>
/// <item>checks again up to <see cref="RecheckRowsPerRun"/> rows an earlier run checked, continuing where the last run's
/// re-check stopped and starting over once it reaches the last checkpoint, so a row changed after it was checked is found
/// within one full cycle;</item>
/// <item>counts the rows up to its checkpoint that the retention purge cannot delete before the next run (all of them
/// without a retention; else those dated no earlier than the purge's cutoff plus <see cref="RowCountMargin"/>), and compares
/// the count the last run recorded with the rows still there: fewer is a checked row deleted, more a row written below a
/// checkpoint (both failures).</item>
/// </list>
/// A row without any seal is reported as unsealed, never as changed, while it was written before rows were sealed: at or
/// below the row id the <see cref="SecurityAuditActions.McpAuditSealingStarted"/> event names (recorded once, when sealing
/// first started), or the first intact sealed row when that is lower or there is no such event, or, during an upgrade, by a node still on the version before:
/// dated (by its own time) within <see cref="UpgradeGrace"/> of that start, and below the first intact sealed row dated
/// after that grace. Any other row without a seal is a changed one. A seal of the next version is reported at Warning
/// (<see cref="McpAuditVerdicts.UnknownVersion"/>). Failures are logged at Error
/// (<see cref="SecurityAuditLogEvents.McpAuditVerificationFailed"/>, 9111); every run writes a checkpoint to the log stream
/// (<see cref="SecurityAuditLogEvents.McpAuditCheckpoint"/>, 9112), and a run that read rows records what it found as a
/// system <see cref="SecurityAuditActions.McpAuditVerified"/> event, itself sealed in the security audit trail.
/// </summary>
public interface IMcpAuditVerificationService
{
    /// <summary>Verifies the MCP audit rows written since the last run, and checks again a slice of those checked before.</summary>
    Task<McpAuditCheckpoint> VerifyNewRowsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What one MCP audit verification covered: rows after <see cref="FromAuditId"/>, every id up to <see cref="MaxAuditId"/>
/// checked or passed as never committed, how many rows it read (<see cref="CheckedCount"/>) and how they verified.
/// </summary>
public sealed record McpAuditCheckpoint(
    int FromAuditId,
    int MaxAuditId,
    string? LastHmac,
    int CheckedCount,
    int ModifiedCount,
    int UnknownKeyCount,
    int OutOfRangeCount,
    int UnsealedCount,
    IReadOnlyList<int> FailedAuditIds)
{
    /// <summary>The first intact sealed row: where sealing started when no sealing-started event says.</summary>
    public int? SealedFromAuditId { get; init; }

    /// <summary>How many ids up to <see cref="MaxAuditId"/> had no row and were passed as never committed.</summary>
    public int NeverCommittedCount { get; init; }

    /// <summary>The id above <see cref="MaxAuditId"/> with no row that is still waited for; null when none is.</summary>
    public int? WaitingForAuditId { get; init; }

    /// <summary>How many rows sealed under the next seal version were read (reported at Warning, not as failures).</summary>
    public int UnknownVersionCount { get; init; }

    /// <summary>How many rows an earlier run checked were checked again (their failures are in the counts above).</summary>
    public int RecheckedCount { get; init; }

    /// <summary>Where the next run's re-check continues: after this row id (0: from the start).</summary>
    public int RecheckedThroughAuditId { get; init; }

    /// <summary>The highest row id written before sealing started, as the sealing-started event names it; null without one.</summary>
    public int? SealingStartedAuditId { get; init; }

    /// <summary>The first intact sealed row dated after the upgrade grace: no row above it may be without a seal.</summary>
    public int? GraceClosedAtAuditId { get; init; }

    /// <summary>How many rows up to the last checkpoint that it counted are no longer there.</summary>
    public int MissingRowCount { get; init; }

    /// <summary>How many rows up to the last checkpoint are there that it did not count.</summary>
    public int AddedRowCount { get; init; }

    /// <summary>The rows up to <see cref="MaxAuditId"/> counted for the next run's comparison; null when none was counted.</summary>
    public int? RetainedRowCount { get; init; }
}

internal sealed class McpAuditVerificationService(
    IDbContextFactory<BeaconContext> contextFactory,
    SecurityAuditIntegrity integrity,
    ISecurityAuditRecorder recorder,
    IOptions<McpDeploymentOptions> deploymentOptions,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory) : IMcpAuditVerificationService
{
    /// <summary>Rows read at a time, each with its statements.</summary>
    internal const int BatchSize = 500;

    /// <summary>The most rows one run checks; a backlog is worked off over the following runs.</summary>
    internal const int MaxRowsPerRun = 50_000;

    /// <summary>The most rows an earlier run checked that one run checks again.</summary>
    internal const int RecheckRowsPerRun = 10_000;

    /// <summary>
    /// How long after sealing started a row without a seal (dated by its own time) is still taken as written by a node
    /// running the version before (an upgrade in progress) rather than as a row whose seal was removed.
    /// </summary>
    internal static readonly TimeSpan UpgradeGrace = TimeSpan.FromDays(1);

    /// <summary>
    /// How far inside the retention the row count starts: rows dated at least this long after the purge's cutoff cannot be
    /// purged before a run that comes within this time of the last one; a longer pause skips one comparison.
    /// </summary>
    internal static readonly TimeSpan RowCountMargin = TimeSpan.FromDays(2);

    internal const string MaxAuditIdDetail = "maxAuditId";
    internal const string SealedFromAuditIdDetail = "sealedFromAuditId";
    internal const string SealedFromTimeDetail = "sealedFromTime";
    internal const string GraceClosedAtAuditIdDetail = "graceClosedAtAuditId";
    internal const string RecheckedThroughAuditIdDetail = "recheckedThroughAuditId";
    internal const string RetainedFromDetail = "retainedFrom";
    internal const string RetainedRowCountDetail = "retainedRowCount";

    /// <summary>The detail the sealing-started event names the highest row id written before it under.</summary>
    internal const string SealingFromAuditIdDetail = "fromAuditId";

    private const int RecentEventsRead = 20;

    private readonly ILogger _auditLogger = loggerFactory.CreateLogger(BeaconTelemetry.AuditLogCategory);

    public async Task<McpAuditCheckpoint> VerifyNewRowsAsync(CancellationToken cancellationToken)
    {
        var run = await VerifyAsync(cancellationToken);
        await ReportAsync(run, cancellationToken);

        return run.Result;
    }

    private async Task<Run> VerifyAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var retentionDays = deploymentOptions.Value.Audit?.RetentionDays;
        var earliest = retentionDays == null ? DateTime.MinValue : now.AddDays(-retentionDays.Value) - SecurityAuditVerification.PurgeLag;
        var latest = now + SecurityAuditVerification.ClockSkew;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var last = await LastCheckpointAsync(context, cancellationToken);
        var marker = await SealingStartedAsync(context, cancellationToken);
        var state = new ScanState(marker, last.SealedFrom, last.GraceClosedAt);
        var lastId = last.MaxAuditId;
        var seen = new List<SecurityAuditVerificationService.SeenEvent>();
        var failed = new List<int>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (seen.Count < MaxRowsPerRun)
        {
            // By id alone: a row is never passed over for the time it carries.
            var (batch, statements) = await BatchAsync(context, lastId, int.MaxValue, BatchSize, cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var row in batch)
            {
                var verdict = VerdictOf(row, statements[row.Id].ToList(), state);
                if (verdict == SecurityAuditVerdicts.Intact && (row.CreatedTime < earliest || row.CreatedTime > latest))
                {
                    verdict = SecurityAuditVerdicts.OutOfRange;
                }

                if (verdict == SecurityAuditVerdicts.Intact)
                {
                    state.Intact(row);
                }

                Count(counts, failed, row.Id, verdict);
                seen.Add(new SecurityAuditVerificationService.SeenEvent(row.Id, row.Hmac ?? string.Empty, verdict == SecurityAuditVerdicts.Intact ? row.CreatedTime : null));
            }

            lastId = batch[^1].Id;
        }

        var advanced = SecurityAuditVerificationService.Advance(last.MaxAuditId, seen, now - SecurityAuditVerificationService.GapGrace);

        // Rows earlier runs checked, again, judged by their seal only (their time was judged when first checked): a row
        // changed after it was checked is found within one full cycle.
        var rechecked = 0;
        var recheckedThrough = last.RecheckedThrough < last.MaxAuditId ? last.RecheckedThrough : 0;
        while (last.MaxAuditId > 0 && rechecked < RecheckRowsPerRun)
        {
            var (batch, statements) = await BatchAsync(context, recheckedThrough, last.MaxAuditId, Math.Min(BatchSize, RecheckRowsPerRun - rechecked), cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var row in batch)
            {
                Count(counts, failed, row.Id, VerdictOf(row, statements[row.Id].ToList(), state));
            }

            rechecked += batch.Count;
            recheckedThrough = batch[^1].Id;
        }

        var (missing, added) = await CompareRowCountAsync(context, last, now, retentionDays, cancellationToken);
        var retainedFrom = retentionDays == null ? (DateTime?)null : WholeSeconds(now.AddDays(-retentionDays.Value) + RowCountMargin);
        int? retainedRowCount = advanced.MaxEventId == 0 ? null : await RowCountAsync(context, advanced.MaxEventId, retainedFrom, cancellationToken);

        var result = new McpAuditCheckpoint(
            last.MaxAuditId,
            advanced.MaxEventId,
            string.IsNullOrEmpty(advanced.LastHmac) ? null : advanced.LastHmac,
            seen.Count,
            counts.GetValueOrDefault(SecurityAuditVerdicts.Modified),
            counts.GetValueOrDefault(SecurityAuditVerdicts.UnknownKey),
            counts.GetValueOrDefault(SecurityAuditVerdicts.OutOfRange),
            counts.GetValueOrDefault(McpAuditVerdicts.Unsealed),
            failed
                .Distinct()
                .Take(SecurityAuditVerificationService.MaxReportedEvents)
                .ToList())
        {
            SealedFromAuditId = state.SealedFrom?.FromAuditId,
            NeverCommittedCount = advanced.NeverCommittedCount,
            WaitingForAuditId = advanced.WaitingForEventId,
            UnknownVersionCount = counts.GetValueOrDefault(McpAuditVerdicts.UnknownVersion),
            RecheckedCount = rechecked,
            RecheckedThroughAuditId = rechecked < RecheckRowsPerRun ? 0 : recheckedThrough,
            SealingStartedAuditId = marker?.FromAuditId,
            GraceClosedAtAuditId = state.GraceClosedAt,
            MissingRowCount = missing,
            AddedRowCount = added,
            RetainedRowCount = retainedRowCount
        };

        return new Run(result, state.SealedFrom?.Time, retainedFrom);
    }

    // The verdict on a row by its seal; a row without one is judged by when sealing started.
    private string VerdictOf(McpAuditLog row, IReadOnlyCollection<McpAuditStatement> statements, ScanState state)
    {
        var verdict = McpAuditSeal.Verify(row, statements, integrity);
        if (verdict != McpAuditVerdicts.Unsealed || state.Start is not { } start || row.Id <= start.FromAuditId)
        {
            return verdict;
        }

        // A node still on the version before dates its rows by its own clock, within the grace of the start, and writes
        // none after a row sealed past that grace.
        var plausible = row.CreatedTime >= start.Time - SecurityAuditVerification.ClockSkew
            && row.CreatedTime <= start.Time + UpgradeGrace + SecurityAuditVerification.ClockSkew
            && (state.GraceClosedAt is not { } closedAt || row.Id < closedAt);

        return plausible ? McpAuditVerdicts.Unsealed : SecurityAuditVerdicts.Modified;
    }

    private static void Count(Dictionary<string, int> counts, List<int> failed, int id, string verdict)
    {
        counts[verdict] = counts.GetValueOrDefault(verdict) + 1;
        if (verdict != SecurityAuditVerdicts.Intact && verdict != McpAuditVerdicts.Unsealed && verdict != McpAuditVerdicts.UnknownVersion)
        {
            failed.Add(id);
        }
    }

    // Rows with ids after afterId up to throughId, in id order, with their statements.
    private static async Task<(List<McpAuditLog> Rows, ILookup<int, McpAuditStatement> Statements)> BatchAsync(
        BeaconContext context,
        int afterId,
        int throughId,
        int take,
        CancellationToken cancellationToken)
    {
        var batch = await context.McpAuditLogs
            .AsNoTracking()
            .Where(x => x.Id > afterId)
            .Where(x => x.Id <= throughId)
            .OrderBy(x => x.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
        if (batch.Count == 0)
        {
            return (batch, Array.Empty<McpAuditStatement>().ToLookup(x => x.McpAuditLogId));
        }

        var firstId = batch[0].Id;
        var lastBatchId = batch[^1].Id;
        var statements = (await context.McpAuditStatements
                .AsNoTracking()
                .Where(x => x.McpAuditLogId >= firstId)
                .Where(x => x.McpAuditLogId <= lastBatchId)
                .ToListAsync(cancellationToken))
            .ToLookup(x => x.McpAuditLogId);

        return (batch, statements);
    }

    // The rows up to the last checkpoint that it counted, against those still there. Compared only while the purge cannot
    // have reached the counted rows since: no retention now, or a run within the margin of the last one.
    private async Task<(int Missing, int Added)> CompareRowCountAsync(
        BeaconContext context,
        LastCheckpoint last,
        DateTime now,
        int? retentionDays,
        CancellationToken cancellationToken)
    {
        if (last.RetainedRowCount is not { } counted || last.MaxAuditId == 0)
        {
            return (0, 0);
        }

        var comparable = retentionDays == null
            || (last.RetainedFrom is { } retainedFrom && now.AddDays(-retentionDays.Value) + SecurityAuditVerification.ClockSkew <= retainedFrom);
        if (!comparable)
        {
            _auditLogger.LogWarning(
                SecurityAuditLogEvents.McpAuditCheckpoint,
                "MCP audit rows up to row {MaxAuditId} were not counted against the last checkpoint: it is older than the margin, or the retention was set since",
                last.MaxAuditId);

            return (0, 0);
        }

        var present = await RowCountAsync(context, last.MaxAuditId, last.RetainedFrom, cancellationToken);

        return (Math.Max(0, counted - present), Math.Max(0, present - counted));
    }

    private static Task<int> RowCountAsync(BeaconContext context, int throughId, DateTime? retainedFrom, CancellationToken cancellationToken)
    {
        var rows = context.McpAuditLogs
            .Where(x => x.Id <= throughId);
        if (retainedFrom is { } from)
        {
            rows = rows.Where(x => x.CreatedTime >= from);
        }

        return rows.CountAsync(cancellationToken);
    }

    // The checkpoint the latest intact MCP audit verification recorded: the id it reached, the first intact sealed row it
    // knew, the first sealed past the grace, where its re-check stopped and the rows it counted. A checkpoint event that
    // does not verify is reported and passed over: a forged one cannot make a run skip rows.
    private async Task<LastCheckpoint> LastCheckpointAsync(BeaconContext context, CancellationToken cancellationToken)
    {
        foreach (var (details, _) in await IntactSystemEventsAsync(context, SecurityAuditActions.McpAuditVerified, newestFirst: true, cancellationToken))
        {
            if (IntOf(details, MaxAuditIdDetail) is not { } maxAuditId)
            {
                continue;
            }

            var sealedFrom = IntOf(details, SealedFromAuditIdDetail) is { } sealedFromId && TimeOf(details, SealedFromTimeDetail) is { } sealedFromTime
                ? new SealingStart(sealedFromId, sealedFromTime)
                : null;

            return new LastCheckpoint(
                maxAuditId,
                sealedFrom,
                IntOf(details, GraceClosedAtAuditIdDetail),
                IntOf(details, RecheckedThroughAuditIdDetail) ?? 0,
                IntOf(details, RetainedRowCountDetail),
                TimeOf(details, RetainedFromDetail));
        }

        return new LastCheckpoint(0, null, null, 0, null, null);
    }

    // When sealing started: the earliest intact sealing-started event, with the highest row id written before it.
    private async Task<SealingStart?> SealingStartedAsync(BeaconContext context, CancellationToken cancellationToken)
    {
        foreach (var (details, createdTime) in await IntactSystemEventsAsync(context, SecurityAuditActions.McpAuditSealingStarted, newestFirst: false, cancellationToken))
        {
            if (IntOf(details, SealingFromAuditIdDetail) is { } fromAuditId)
            {
                return new SealingStart(fromAuditId, createdTime);
            }
        }

        return null;
    }

    private async Task<List<(JsonNode? Details, DateTime CreatedTime)>> IntactSystemEventsAsync(
        BeaconContext context,
        string action,
        bool newestFirst,
        CancellationToken cancellationToken)
    {
        var events = context.SecurityAuditEvents
            .AsNoTracking()
            .Where(x => x.Action == action)
            .Where(x => x.ActorKind == SecurityAuditActorKinds.System)
            .Where(x => x.ActorUserId == null);
        var recent = await (newestFirst ? events.OrderByDescending(x => x.Id) : events.OrderBy(x => x.Id))
            .Take(RecentEventsRead)
            .ToListAsync(cancellationToken);
        var intact = new List<(JsonNode? Details, DateTime CreatedTime)>();
        foreach (var auditEvent in recent)
        {
            if (integrity.Verify(auditEvent) != SecurityAuditVerdict.Intact)
            {
                _auditLogger.LogError(
                    SecurityAuditLogEvents.McpAuditVerificationFailed,
                    "MCP audit verification event {AuditId} fails verification and is passed over",
                    auditEvent.Id);
                BeaconTelemetry.SecurityAuditVerificationFailures.Add(1, new KeyValuePair<string, object?>(BeaconTelemetry.SecurityAuditActionTag, action));

                continue;
            }

            intact.Add((string.IsNullOrEmpty(auditEvent.DetailsJson) ? null : JsonNode.Parse(auditEvent.DetailsJson), auditEvent.CreatedTime));
        }

        return intact;
    }

    private static int? IntOf(JsonNode? details, string key)
    {
        return details?[key] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;
    }

    private static DateTime? TimeOf(JsonNode? details, string key)
    {
        return details?[key] is JsonValue value
            && value.TryGetValue<string>(out var text)
            && DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time)
                ? time
                : null;
    }

    // The details keep times to the second: rows are counted from the time the next run reads back.
    private static DateTime WholeSeconds(DateTime time)
    {
        return new DateTime(time.Ticks - (time.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
    }

    private async Task ReportAsync(Run run, CancellationToken cancellationToken)
    {
        var checkpoint = run.Result;
        var failedCount = checkpoint.ModifiedCount + checkpoint.UnknownKeyCount + checkpoint.OutOfRangeCount + checkpoint.MissingRowCount + checkpoint.AddedRowCount;
        if (failedCount > 0)
        {
            _auditLogger.LogError(
                SecurityAuditLogEvents.McpAuditVerificationFailed,
                "MCP audit verification found {FailedCount} failures (modified {ModifiedCount}, unknown key {UnknownKeyCount}, out of range {OutOfRangeCount}, rows missing {MissingRowCount}, rows added {AddedRowCount}) after row {FromAuditId} and in {RecheckedCount} rows checked again; first ids {FailedAuditIds}",
                failedCount,
                checkpoint.ModifiedCount,
                checkpoint.UnknownKeyCount,
                checkpoint.OutOfRangeCount,
                checkpoint.MissingRowCount,
                checkpoint.AddedRowCount,
                checkpoint.FromAuditId,
                checkpoint.RecheckedCount,
                string.Join(",", checkpoint.FailedAuditIds));
            BeaconTelemetry.SecurityAuditVerificationFailures.Add(
                failedCount,
                new KeyValuePair<string, object?>(BeaconTelemetry.SecurityAuditActionTag, SecurityAuditActions.McpAuditVerified));
        }

        if (checkpoint.UnknownVersionCount > 0)
        {
            _auditLogger.LogWarning(
                SecurityAuditLogEvents.McpAuditCheckpoint,
                "MCP audit verification read {UnknownVersionCount} rows sealed under seal version {SealVersion}, which this node does not verify: they verify once it runs that version",
                checkpoint.UnknownVersionCount,
                McpAuditSeal.CurrentVersion + 1);
        }

        _auditLogger.Log(
            LogLevel.Information,
            SecurityAuditLogEvents.McpAuditCheckpoint,
            "MCP audit checkpoint: FromAuditId={FromAuditId} MaxAuditId={MaxAuditId} Checked={Checked} Rechecked={Rechecked} RecheckedThroughAuditId={RecheckedThroughAuditId} Failed={Failed} Unsealed={Unsealed} UnknownVersion={UnknownVersion} LastHmac={LastHmac} KeyId={KeyId} SealedFromAuditId={SealedFromAuditId} NeverCommitted={NeverCommittedCount} WaitingForAuditId={WaitingForAuditId} RetainedRowCount={RetainedRowCount}",
            checkpoint.FromAuditId,
            checkpoint.MaxAuditId,
            checkpoint.CheckedCount,
            checkpoint.RecheckedCount,
            checkpoint.RecheckedThroughAuditId,
            failedCount,
            checkpoint.UnsealedCount,
            checkpoint.UnknownVersionCount,
            checkpoint.LastHmac,
            integrity.CurrentKeyId,
            checkpoint.SealedFromAuditId,
            checkpoint.NeverCommittedCount,
            checkpoint.WaitingForAuditId,
            checkpoint.RetainedRowCount);

        if (checkpoint.CheckedCount + checkpoint.RecheckedCount + checkpoint.MissingRowCount == 0)
        {
            return;
        }

        await recorder.RecordAsync(
            new SecurityAuditEntry(SecurityAuditActions.McpAuditVerified)
                .On(SecurityAuditResources.McpAudit, null)
                .Ending(failedCount > 0 ? SecurityAuditOutcomes.Failure : SecurityAuditOutcomes.Success)
                .By(SecurityAuditActor.System)
                .With(new SecurityAuditDetails()
                    .Add("scheduled", true)
                    .Add("fromAuditId", checkpoint.FromAuditId)
                    .Add(MaxAuditIdDetail, checkpoint.MaxAuditId)
                    .Add("checkedCount", checkpoint.CheckedCount)
                    .Add("recheckedCount", checkpoint.RecheckedCount)
                    .Add(RecheckedThroughAuditIdDetail, checkpoint.RecheckedThroughAuditId)
                    .Add("modifiedCount", checkpoint.ModifiedCount)
                    .Add("unknownKeyCount", checkpoint.UnknownKeyCount)
                    .Add("outOfRangeCount", checkpoint.OutOfRangeCount)
                    .Add("unsealedCount", checkpoint.UnsealedCount)
                    .Add("unknownVersionCount", checkpoint.UnknownVersionCount)
                    .Add("missingRowCount", checkpoint.MissingRowCount)
                    .Add("addedRowCount", checkpoint.AddedRowCount)
                    .Add("failedAuditIds", checkpoint.FailedAuditIds)
                    .Add(SealedFromAuditIdDetail, checkpoint.SealedFromAuditId)
                    .Add(SealedFromTimeDetail, run.SealedFromTime)
                    .Add(GraceClosedAtAuditIdDetail, checkpoint.GraceClosedAtAuditId)
                    .Add(RetainedFromDetail, run.RetainedFrom)
                    .Add(RetainedRowCountDetail, checkpoint.RetainedRowCount)
                    .Add("neverCommittedCount", checkpoint.NeverCommittedCount)
                    .Add("waitingForAuditId", checkpoint.WaitingForAuditId)),
            cancellationToken);
    }

    // Where sealing started: the highest row id written before it (rows at or below it may carry no seal), and when.
    private sealed record SealingStart(int FromAuditId, DateTime Time);

    private sealed record LastCheckpoint(
        int MaxAuditId,
        SealingStart? SealedFrom,
        int? GraceClosedAt,
        int RecheckedThrough,
        int? RetainedRowCount,
        DateTime? RetainedFrom);

    private sealed record Run(McpAuditCheckpoint Result, DateTime? SealedFromTime, DateTime? RetainedFrom);

    // What the scan learns in id order: the first intact sealed row, and the first intact sealed row dated after the upgrade
    // grace. Sealing started at the earlier of the sealing-started event and the first intact sealed row an earlier run
    // recorded, so an event recorded again later (the first one gone) cannot move the start past rows already sealed.
    private sealed class ScanState(SealingStart? marker, SealingStart? sealedFrom, int? graceClosedAt)
    {
        public SealingStart? SealedFrom { get; private set; } = sealedFrom;

        public int? GraceClosedAt { get; private set; } = graceClosedAt;

        public SealingStart? Start => marker == null || (SealedFrom != null && SealedFrom.FromAuditId < marker.FromAuditId) ? SealedFrom : marker;

        public void Intact(McpAuditLog row)
        {
            SealedFrom ??= new SealingStart(row.Id, row.CreatedTime);
            if (GraceClosedAt == null && Start is { } start && row.CreatedTime > start.Time + UpgradeGrace + SecurityAuditVerification.ClockSkew)
            {
                GraceClosedAt = row.Id;
            }
        }
    }
}
