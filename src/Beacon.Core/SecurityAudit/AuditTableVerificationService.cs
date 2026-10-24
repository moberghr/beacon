using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Telemetry;

namespace Beacon.Core.SecurityAudit;

/// <summary>
/// Verifies the rows of the sealed audit tables beside the MCP audit (<see cref="AuditTableStores"/>: the execution log, then
/// the settings history) against their seals (<see cref="AuditTableSeal"/>), the way the MCP audit verification checks its
/// rows (<see cref="IMcpAuditVerificationService"/>), and runs as part of the scheduled security audit verification
/// (<see cref="ISecurityAuditVerificationService.VerifyNewEventsAsync"/>). For each table, each run:
/// <list type="bullet">
/// <item>continues from the highest row id the table's last intact verified event by the system names, checks every newer
/// row in id order (never selected by its time), waits for a missing id and passes it as never committed after the grace;</item>
/// <item>checks again up to <see cref="RecheckRowsPerRun"/> rows an earlier run checked, continuing where the last run's
/// re-check stopped and starting over once it reaches the last checkpoint;</item>
/// <item>counts the rows up to its checkpoint that a retention purge cannot delete before the next run (all of them while
/// the table has no retention, <see cref="IAuditTableRetention"/>; else those dated no earlier than the purge's cutoff plus
/// <see cref="RowCountMargin"/>), and compares the count the last run recorded with the rows still there: fewer is a checked
/// row deleted, more a row written below a checkpoint (both failures). A row the purge deleted past the retention is never
/// counted, so it never fails the verification.</item>
/// </list>
/// A row without any seal is unsealed, never changed, while it was written before the table's rows were sealed (at or below
/// the row id the table's sealing-started event names, or the first intact sealed row when that is lower or there is no
/// such event), or during an upgrade by a node still on the version before (dated within <see cref="UpgradeGrace"/> of that
/// start, and below the first intact sealed row dated after that grace). Any other row without a seal is a changed one. A
/// seal of the next version is reported at Warning. Failures are logged at Error
/// (<see cref="SecurityAuditLogEvents.AuditTableVerificationFailed"/>, 9113); every run writes a checkpoint per table to the
/// log stream (<see cref="SecurityAuditLogEvents.AuditTableCheckpoint"/>, 9114), and a run that read rows of a table
/// records what it found as that table's system verified event, itself sealed in the security audit trail.
/// </summary>
public interface IAuditTableVerificationService
{
    /// <summary>Verifies each sealed table's rows written since the last run, and checks again a slice of those checked before.</summary>
    Task<IReadOnlyList<AuditTableCheckpoint>> VerifyNewRowsAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What one verification of a sealed audit table (<see cref="Store"/>) covered: rows after <see cref="FromRowId"/>, every id
/// up to <see cref="MaxRowId"/> checked or passed as never committed, how many rows it read and how they verified.
/// </summary>
public sealed record AuditTableCheckpoint(
    string Store,
    int FromRowId,
    int MaxRowId,
    string? LastHmac,
    int CheckedCount,
    int ModifiedCount,
    int UnknownKeyCount,
    int OutOfRangeCount,
    int UnsealedCount,
    IReadOnlyList<int> FailedRowIds)
{
    /// <summary>The first intact sealed row: where sealing started when no sealing-started event says.</summary>
    public int? SealedFromRowId { get; init; }

    /// <summary>How many ids up to <see cref="MaxRowId"/> had no row and were passed as never committed.</summary>
    public int NeverCommittedCount { get; init; }

    /// <summary>The id above <see cref="MaxRowId"/> with no row that is still waited for; null when none is.</summary>
    public int? WaitingForRowId { get; init; }

    /// <summary>How many rows sealed under the next seal version were read (reported at Warning, not as failures).</summary>
    public int UnknownVersionCount { get; init; }

    /// <summary>How many rows an earlier run checked were checked again (their failures are in the counts above).</summary>
    public int RecheckedCount { get; init; }

    /// <summary>Where the next run's re-check continues: after this row id (0: from the start).</summary>
    public int RecheckedThroughRowId { get; init; }

    /// <summary>The highest row id written before sealing started, as the sealing-started event names it; null without one.</summary>
    public int? SealingStartedRowId { get; init; }

    /// <summary>The first intact sealed row dated after the upgrade grace: no row above it may be without a seal.</summary>
    public int? GraceClosedAtRowId { get; init; }

    /// <summary>How many rows up to the last checkpoint that it counted are no longer there.</summary>
    public int MissingRowCount { get; init; }

    /// <summary>How many rows up to the last checkpoint are there that it did not count.</summary>
    public int AddedRowCount { get; init; }

    /// <summary>The rows up to <see cref="MaxRowId"/> counted for the next run's comparison; null when none was counted.</summary>
    public int? RetainedRowCount { get; init; }

    /// <summary>
    /// How many intact rows it read that are older than the table's retention allows: the purge has not deleted them yet
    /// (the retention job is late or not scheduled). Reported at Warning, never as a failure.
    /// </summary>
    public int PastRetentionCount { get; init; }

    /// <summary>True when the table lacks its seal columns (the seal migration is not applied): nothing was verified.</summary>
    public bool SealColumnsMissing { get; init; }

    /// <summary>The failures: changed rows, unknown keys, rows dated in the future, rows missing and rows added.</summary>
    public int FailedCount => ModifiedCount + UnknownKeyCount + OutOfRangeCount + MissingRowCount + AddedRowCount;
}

/// <summary>
/// How long a sealed audit table's rows are kept, by the table's store name (<c>execution_log</c>, <c>setting_history</c>):
/// null while nothing purges them.
/// The verification counts only the rows a purge cannot reach before its next run. The default reads the data retention
/// job's windows (<c>Beacon:Retention</c>); registered with <c>TryAdd</c>, so a host that purges one of these tables
/// another way replaces it.
/// </summary>
public interface IAuditTableRetention
{
    /// <summary>The days the table's rows are kept, or null when its rows are never purged.</summary>
    int? RetentionDaysOf(string store);
}

/// <summary>
/// The windows of the data retention job (<c>Beacon:Retention</c>): the execution log is its <c>execution-log</c> target
/// (<see cref="Configuration.RetentionOptions.DaysFor"/>, <c>0</c> keeping it forever); nothing purges the settings history.
/// </summary>
internal sealed class DataRetentionAuditTableRetention(Microsoft.Extensions.Options.IOptions<Configuration.RetentionOptions> options) : IAuditTableRetention
{
    public int? RetentionDaysOf(string store)
    {
        if (store != AuditTableStores.ExecutionLog.Name)
        {
            return null;
        }

        var days = options.Value.DaysFor(Services.Retention.RetentionTargetNames.ExecutionLog);

        return days > 0 ? days : null;
    }
}

internal sealed class AuditTableVerificationService(
    IDbContextFactory<BeaconContext> contextFactory,
    SecurityAuditIntegrity integrity,
    ISecurityAuditRecorder recorder,
    IAuditTableRetention retention,
    AuditTableSealSchema sealSchema,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory) : IAuditTableVerificationService
{
    /// <summary>Rows read at a time.</summary>
    internal const int BatchSize = 1000;

    /// <summary>The most rows of one table one run checks; a backlog is worked off over the following runs.</summary>
    internal const int MaxRowsPerRun = 50_000;

    /// <summary>The most rows of one table an earlier run checked that one run checks again.</summary>
    internal const int RecheckRowsPerRun = 10_000;

    /// <summary>
    /// How long after sealing started a row without a seal (dated by its own time) is still taken as written by a node
    /// running the version before (an upgrade in progress) rather than as a row whose seal was removed.
    /// </summary>
    internal static readonly TimeSpan UpgradeGrace = McpAuditVerificationService.UpgradeGrace;

    /// <summary>How far inside the retention the row count starts (see <see cref="McpAuditVerificationService.RowCountMargin"/>).</summary>
    internal static readonly TimeSpan RowCountMargin = McpAuditVerificationService.RowCountMargin;

    internal const string MaxRowIdDetail = "maxRowId";
    internal const string SealedFromRowIdDetail = "sealedFromRowId";
    internal const string SealedFromTimeDetail = "sealedFromTime";
    internal const string GraceClosedAtRowIdDetail = "graceClosedAtRowId";
    internal const string RecheckedThroughRowIdDetail = "recheckedThroughRowId";
    internal const string RetainedFromDetail = "retainedFrom";
    internal const string RetainedRowCountDetail = "retainedRowCount";

    /// <summary>The detail a sealing-started event names the highest row id written before it under.</summary>
    internal const string SealingFromRowIdDetail = "fromRowId";

    private const int RecentEventsRead = 20;

    private readonly ILogger _auditLogger = loggerFactory.CreateLogger(BeaconTelemetry.AuditLogCategory);

    public async Task<IReadOnlyList<AuditTableCheckpoint>> VerifyNewRowsAsync(CancellationToken cancellationToken)
    {
        return
        [
            await VerifyStoreAsync(AuditTableStores.ExecutionLog, cancellationToken),
            await VerifyStoreAsync(AuditTableStores.SettingHistory, cancellationToken)
        ];
    }

    internal async Task<AuditTableCheckpoint> VerifyStoreAsync<T>(AuditTableStore<T> store, CancellationToken cancellationToken)
        where T : BaseEntity, ISealedAuditRow
    {
        await using (var context = await contextFactory.CreateDbContextAsync(cancellationToken))
        {
            // Before the seal migration the table has no seal to verify: passed over (the schema logs it once).
            if (await sealSchema.SealColumnsMissingAsync(context, typeof(T), cancellationToken))
            {
                return new AuditTableCheckpoint(store.Name, 0, 0, null, 0, 0, 0, 0, 0, []) { SealColumnsMissing = true };
            }
        }

        var run = await VerifyAsync(store, cancellationToken);
        await ReportAsync(store, run, cancellationToken);

        return run.Result;
    }

    private async Task<Run> VerifyAsync<T>(AuditTableStore<T> store, CancellationToken cancellationToken)
        where T : BaseEntity, ISealedAuditRow
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var retentionDays = retention.RetentionDaysOf(store.Name);
        var earliest = retentionDays == null ? DateTime.MinValue : now.AddDays(-retentionDays.Value) - SecurityAuditVerification.PurgeLag;
        var latest = now + SecurityAuditVerification.ClockSkew;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var last = await LastCheckpointAsync(context, store, cancellationToken);
        var marker = await SealingStartedAsync(context, store, cancellationToken);
        var state = new ScanState(marker, last.SealedFrom, last.GraceClosedAt);
        var lastId = last.MaxRowId;
        var seen = new List<SecurityAuditVerificationService.SeenEvent>();
        var failed = new List<int>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var pastRetention = 0;
        while (seen.Count < MaxRowsPerRun)
        {
            // By id alone: a row is never passed over for the time it carries.
            var batch = await BatchAsync(context, store, lastId, int.MaxValue, BatchSize, cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var row in batch)
            {
                var verdict = VerdictOf(row, store, state);
                if (verdict == SecurityAuditVerdicts.Intact && row.CreatedTime > latest)
                {
                    verdict = SecurityAuditVerdicts.OutOfRange;
                }

                // Older than the retention allows: the purge has not reached it (the retention job is late or not
                // scheduled). Its seal holds, so it is reported at Warning, never as a failure.
                if (verdict == SecurityAuditVerdicts.Intact && row.CreatedTime < earliest)
                {
                    pastRetention++;
                }

                if (verdict == SecurityAuditVerdicts.Intact)
                {
                    state.Intact(row.Id, row.CreatedTime);
                }

                Count(counts, failed, row.Id, verdict);
                seen.Add(new SecurityAuditVerificationService.SeenEvent(row.Id, row.Hmac ?? string.Empty, verdict == SecurityAuditVerdicts.Intact ? row.CreatedTime : null));
            }

            lastId = batch[^1].Id;
        }

        var advanced = SecurityAuditVerificationService.Advance(last.MaxRowId, seen, now - SecurityAuditVerificationService.GapGrace);

        // Rows earlier runs checked, again, judged by their seal only (their time was judged when first checked).
        var rechecked = 0;
        var recheckedThrough = last.RecheckedThrough < last.MaxRowId ? last.RecheckedThrough : 0;
        while (last.MaxRowId > 0 && rechecked < RecheckRowsPerRun)
        {
            var batch = await BatchAsync(context, store, recheckedThrough, last.MaxRowId, Math.Min(BatchSize, RecheckRowsPerRun - rechecked), cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var row in batch)
            {
                Count(counts, failed, row.Id, VerdictOf(row, store, state));
            }

            rechecked += batch.Count;
            recheckedThrough = batch[^1].Id;
        }

        var (missing, added) = await CompareRowCountAsync(context, store, last, now, retentionDays, cancellationToken);
        var retainedFrom = retentionDays == null ? (DateTime?)null : WholeSeconds(now.AddDays(-retentionDays.Value) + RowCountMargin);
        int? retainedRowCount = advanced.MaxEventId == 0 ? null : await RowCountAsync(context, store, advanced.MaxEventId, retainedFrom, cancellationToken);

        var result = new AuditTableCheckpoint(
            store.Name,
            last.MaxRowId,
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
            SealedFromRowId = state.SealedFrom?.FromRowId,
            NeverCommittedCount = advanced.NeverCommittedCount,
            WaitingForRowId = advanced.WaitingForEventId,
            UnknownVersionCount = counts.GetValueOrDefault(McpAuditVerdicts.UnknownVersion),
            RecheckedCount = rechecked,
            RecheckedThroughRowId = rechecked < RecheckRowsPerRun ? 0 : recheckedThrough,
            SealingStartedRowId = marker?.FromRowId,
            GraceClosedAtRowId = state.GraceClosedAt,
            MissingRowCount = missing,
            AddedRowCount = added,
            RetainedRowCount = retainedRowCount,
            PastRetentionCount = pastRetention
        };

        return new Run(result, state.SealedFrom?.Time, retainedFrom);
    }

    // The verdict on a row by its seal; a row without one is judged by when sealing started.
    private string VerdictOf<T>(T row, AuditTableStore<T> store, ScanState state)
        where T : BaseEntity, ISealedAuditRow
    {
        var verdict = AuditTableSeal.Verify(row, store, integrity);
        if (verdict != McpAuditVerdicts.Unsealed || state.Start is not { } start || row.Id <= start.FromRowId)
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

    // Rows with ids after afterId up to throughId, in id order.
    private static Task<List<T>> BatchAsync<T>(
        BeaconContext context,
        AuditTableStore<T> store,
        int afterId,
        int throughId,
        int take,
        CancellationToken cancellationToken)
        where T : BaseEntity, ISealedAuditRow
    {
        return store.Rows(context)
            .AsNoTracking()
            .Where(x => x.Id > afterId)
            .Where(x => x.Id <= throughId)
            .OrderBy(x => x.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    // The rows up to the last checkpoint that it counted, against those still there. Compared only while a purge cannot
    // have reached the counted rows since: no retention now, or a run within the margin of the last one.
    private async Task<(int Missing, int Added)> CompareRowCountAsync<T>(
        BeaconContext context,
        AuditTableStore<T> store,
        LastCheckpoint last,
        DateTime now,
        int? retentionDays,
        CancellationToken cancellationToken)
        where T : BaseEntity, ISealedAuditRow
    {
        if (last.RetainedRowCount is not { } counted || last.MaxRowId == 0)
        {
            return (0, 0);
        }

        var comparable = retentionDays == null
            || (last.RetainedFrom is { } retainedFrom && now.AddDays(-retentionDays.Value) + SecurityAuditVerification.ClockSkew <= retainedFrom);
        if (!comparable)
        {
            _auditLogger.LogWarning(
                SecurityAuditLogEvents.AuditTableCheckpoint,
                "Audit table {Store} rows up to row {MaxRowId} were not counted against the last checkpoint: it is older than the margin, or the retention was set since",
                store.Name,
                last.MaxRowId);

            return (0, 0);
        }

        var present = await RowCountAsync(context, store, last.MaxRowId, last.RetainedFrom, cancellationToken);

        return (Math.Max(0, counted - present), Math.Max(0, present - counted));
    }

    private static Task<int> RowCountAsync<T>(BeaconContext context, AuditTableStore<T> store, int throughId, DateTime? retainedFrom, CancellationToken cancellationToken)
        where T : BaseEntity, ISealedAuditRow
    {
        var rows = store.Rows(context)
            .Where(x => x.Id <= throughId);
        if (retainedFrom is { } from)
        {
            rows = rows.Where(x => x.CreatedTime >= from);
        }

        return rows.CountAsync(cancellationToken);
    }

    // The checkpoint the table's latest intact verified event recorded. An event that does not verify is reported and
    // passed over: a forged one cannot make a run skip rows.
    private async Task<LastCheckpoint> LastCheckpointAsync<T>(BeaconContext context, AuditTableStore<T> store, CancellationToken cancellationToken)
        where T : BaseEntity, ISealedAuditRow
    {
        foreach (var (details, _) in await IntactSystemEventsAsync(context, store.VerifiedAction, newestFirst: true, cancellationToken))
        {
            if (IntOf(details, MaxRowIdDetail) is not { } maxRowId)
            {
                continue;
            }

            var sealedFrom = IntOf(details, SealedFromRowIdDetail) is { } sealedFromId && TimeOf(details, SealedFromTimeDetail) is { } sealedFromTime
                ? new SealingStart(sealedFromId, sealedFromTime)
                : null;

            return new LastCheckpoint(
                maxRowId,
                sealedFrom,
                IntOf(details, GraceClosedAtRowIdDetail),
                IntOf(details, RecheckedThroughRowIdDetail) ?? 0,
                IntOf(details, RetainedRowCountDetail),
                TimeOf(details, RetainedFromDetail));
        }

        return new LastCheckpoint(0, null, null, 0, null, null);
    }

    // When the table's sealing started: the earliest intact sealing-started event, with the highest row id written before it.
    private async Task<SealingStart?> SealingStartedAsync<T>(BeaconContext context, AuditTableStore<T> store, CancellationToken cancellationToken)
        where T : BaseEntity, ISealedAuditRow
    {
        foreach (var (details, createdTime) in await IntactSystemEventsAsync(context, store.SealingStartedAction, newestFirst: false, cancellationToken))
        {
            if (IntOf(details, SealingFromRowIdDetail) is { } fromRowId)
            {
                return new SealingStart(fromRowId, createdTime);
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
                    SecurityAuditLogEvents.AuditTableVerificationFailed,
                    "Audit table verification event {AuditId} fails verification and is passed over",
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

    private async Task ReportAsync<T>(AuditTableStore<T> store, Run run, CancellationToken cancellationToken)
        where T : BaseEntity, ISealedAuditRow
    {
        var checkpoint = run.Result;
        var failedCount = checkpoint.FailedCount;
        if (failedCount > 0)
        {
            _auditLogger.LogError(
                SecurityAuditLogEvents.AuditTableVerificationFailed,
                "Audit table {Store} verification found {FailedCount} failures (modified {ModifiedCount}, unknown key {UnknownKeyCount}, out of range {OutOfRangeCount}, rows missing {MissingRowCount}, rows added {AddedRowCount}) after row {FromRowId} and in {RecheckedCount} rows checked again; first ids {FailedRowIds}",
                store.Name,
                failedCount,
                checkpoint.ModifiedCount,
                checkpoint.UnknownKeyCount,
                checkpoint.OutOfRangeCount,
                checkpoint.MissingRowCount,
                checkpoint.AddedRowCount,
                checkpoint.FromRowId,
                checkpoint.RecheckedCount,
                string.Join(",", checkpoint.FailedRowIds));
            BeaconTelemetry.SecurityAuditVerificationFailures.Add(
                failedCount,
                new KeyValuePair<string, object?>(BeaconTelemetry.SecurityAuditActionTag, store.VerifiedAction));
        }

        if (checkpoint.PastRetentionCount > 0)
        {
            _auditLogger.LogWarning(
                SecurityAuditLogEvents.AuditTableCheckpoint,
                "Audit table {Store} verification read {PastRetentionCount} intact rows older than its retention of {RetentionDays} days: the data retention job has not purged them; check that it is scheduled and runs",
                store.Name,
                checkpoint.PastRetentionCount,
                retention.RetentionDaysOf(store.Name));
        }

        if (checkpoint.UnknownVersionCount > 0)
        {
            _auditLogger.LogWarning(
                SecurityAuditLogEvents.AuditTableCheckpoint,
                "Audit table {Store} verification read {UnknownVersionCount} rows sealed under seal version {SealVersion}, which this node does not verify: they verify once it runs that version",
                store.Name,
                checkpoint.UnknownVersionCount,
                AuditTableSeal.CurrentVersion + 1);
        }

        _auditLogger.Log(
            LogLevel.Information,
            SecurityAuditLogEvents.AuditTableCheckpoint,
            "Audit table checkpoint: Store={Store} FromRowId={FromRowId} MaxRowId={MaxRowId} Checked={Checked} Rechecked={Rechecked} RecheckedThroughRowId={RecheckedThroughRowId} Failed={Failed} Unsealed={Unsealed} UnknownVersion={UnknownVersion} LastHmac={LastHmac} KeyId={KeyId} SealedFromRowId={SealedFromRowId} NeverCommitted={NeverCommittedCount} WaitingForRowId={WaitingForRowId} RetainedRowCount={RetainedRowCount}",
            store.Name,
            checkpoint.FromRowId,
            checkpoint.MaxRowId,
            checkpoint.CheckedCount,
            checkpoint.RecheckedCount,
            checkpoint.RecheckedThroughRowId,
            failedCount,
            checkpoint.UnsealedCount,
            checkpoint.UnknownVersionCount,
            checkpoint.LastHmac,
            integrity.CurrentKeyId,
            checkpoint.SealedFromRowId,
            checkpoint.NeverCommittedCount,
            checkpoint.WaitingForRowId,
            checkpoint.RetainedRowCount);

        if (checkpoint.CheckedCount + checkpoint.RecheckedCount + checkpoint.MissingRowCount == 0)
        {
            return;
        }

        await recorder.RecordAsync(
            new SecurityAuditEntry(store.VerifiedAction)
                .On(store.Resource, null)
                .Ending(failedCount > 0 ? SecurityAuditOutcomes.Failure : SecurityAuditOutcomes.Success)
                .By(SecurityAuditActor.System)
                .With(new SecurityAuditDetails()
                    .Add("scheduled", true)
                    .Add("fromRowId", checkpoint.FromRowId)
                    .Add(MaxRowIdDetail, checkpoint.MaxRowId)
                    .Add("checkedCount", checkpoint.CheckedCount)
                    .Add("recheckedCount", checkpoint.RecheckedCount)
                    .Add(RecheckedThroughRowIdDetail, checkpoint.RecheckedThroughRowId)
                    .Add("modifiedCount", checkpoint.ModifiedCount)
                    .Add("unknownKeyCount", checkpoint.UnknownKeyCount)
                    .Add("outOfRangeCount", checkpoint.OutOfRangeCount)
                    .Add("pastRetentionCount", checkpoint.PastRetentionCount)
                    .Add("unsealedCount", checkpoint.UnsealedCount)
                    .Add("unknownVersionCount", checkpoint.UnknownVersionCount)
                    .Add("missingRowCount", checkpoint.MissingRowCount)
                    .Add("addedRowCount", checkpoint.AddedRowCount)
                    .Add("failedRowIds", checkpoint.FailedRowIds)
                    .Add(SealedFromRowIdDetail, checkpoint.SealedFromRowId)
                    .Add(SealedFromTimeDetail, run.SealedFromTime)
                    .Add(GraceClosedAtRowIdDetail, checkpoint.GraceClosedAtRowId)
                    .Add(RetainedFromDetail, run.RetainedFrom)
                    .Add(RetainedRowCountDetail, checkpoint.RetainedRowCount)
                    .Add("neverCommittedCount", checkpoint.NeverCommittedCount)
                    .Add("waitingForRowId", checkpoint.WaitingForRowId)),
            cancellationToken);
    }

    // Where sealing started: the highest row id written before it (rows at or below it may carry no seal), and when.
    private sealed record SealingStart(int FromRowId, DateTime Time);

    private sealed record LastCheckpoint(
        int MaxRowId,
        SealingStart? SealedFrom,
        int? GraceClosedAt,
        int RecheckedThrough,
        int? RetainedRowCount,
        DateTime? RetainedFrom);

    private sealed record Run(AuditTableCheckpoint Result, DateTime? SealedFromTime, DateTime? RetainedFrom);

    // What the scan learns in id order: the first intact sealed row, and the first intact sealed row dated after the upgrade
    // grace. Sealing started at the earlier of the sealing-started event and the first intact sealed row an earlier run
    // recorded, so an event recorded again later cannot move the start past rows already sealed.
    private sealed class ScanState(SealingStart? marker, SealingStart? sealedFrom, int? graceClosedAt)
    {
        public SealingStart? SealedFrom { get; private set; } = sealedFrom;

        public int? GraceClosedAt { get; private set; } = graceClosedAt;

        public SealingStart? Start => marker == null || (SealedFrom != null && SealedFrom.FromRowId < marker.FromRowId) ? SealedFrom : marker;

        public void Intact(int id, DateTime createdTime)
        {
            SealedFrom ??= new SealingStart(id, createdTime);
            if (GraceClosedAt == null && Start is { } start && createdTime > start.Time + UpgradeGrace + SecurityAuditVerification.ClockSkew)
            {
                GraceClosedAt = id;
            }
        }
    }
}
