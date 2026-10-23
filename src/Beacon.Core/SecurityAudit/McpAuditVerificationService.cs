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
/// Verifies the MCP audit rows written since the last run against their seals (<see cref="McpAuditSeal"/>), the way the
/// scheduled security audit verification checks its events, and runs as part of it
/// (<see cref="ISecurityAuditVerificationService.VerifyNewEventsAsync"/>), so a host that schedules that job verifies both.
/// It continues from the highest row id the last intact <see cref="SecurityAuditActions.McpAuditVerified"/> event by the
/// system names, checks every newer row in id order (never selected by its time), waits for a missing id and passes it as
/// never committed after the grace, as that verification does. A row without any seal is reported as unsealed, never as
/// changed, while it was written before rows were sealed: below the first intact sealed row, or within
/// <see cref="UpgradeGrace"/> of it (an older node during an upgrade); later, a row without a seal is a changed one.
/// Failures are logged at Error (<see cref="SecurityAuditLogEvents.McpAuditVerificationFailed"/>, 9111); every run writes a
/// checkpoint to the log stream (<see cref="SecurityAuditLogEvents.McpAuditCheckpoint"/>, 9112), and a run that read rows
/// records what it found as a system <see cref="SecurityAuditActions.McpAuditVerified"/> event, itself sealed in the
/// security audit trail.
/// </summary>
public interface IMcpAuditVerificationService
{
    /// <summary>Verifies the MCP audit rows written since the last run.</summary>
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
    /// <summary>The first intact sealed row: rows without a seal above it (past the upgrade grace) are changed ones.</summary>
    public int? SealedFromAuditId { get; init; }

    /// <summary>How many ids up to <see cref="MaxAuditId"/> had no row and were passed as never committed.</summary>
    public int NeverCommittedCount { get; init; }

    /// <summary>The id above <see cref="MaxAuditId"/> with no row that is still waited for; null when none is.</summary>
    public int? WaitingForAuditId { get; init; }
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

    /// <summary>
    /// How long after the first intact sealed row a row without a seal is still taken as written by a node running the
    /// version before (an upgrade in progress) rather than as a row whose seal was removed.
    /// </summary>
    internal static readonly TimeSpan UpgradeGrace = TimeSpan.FromDays(1);

    internal const string MaxAuditIdDetail = "maxAuditId";
    internal const string SealedFromAuditIdDetail = "sealedFromAuditId";
    internal const string SealedFromTimeDetail = "sealedFromTime";

    private const int RecentCheckpointsRead = 20;

    private readonly ILogger _auditLogger = loggerFactory.CreateLogger(BeaconTelemetry.AuditLogCategory);

    public async Task<McpAuditCheckpoint> VerifyNewRowsAsync(CancellationToken cancellationToken)
    {
        var checkpoint = await VerifyAsync(cancellationToken);
        await ReportAsync(checkpoint.Result, checkpoint.SealedFromTime, cancellationToken);

        return checkpoint.Result;
    }

    private async Task<(McpAuditCheckpoint Result, DateTime? SealedFromTime)> VerifyAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var retentionDays = deploymentOptions.Value.Audit?.RetentionDays;
        var earliest = retentionDays == null ? DateTime.MinValue : now.AddDays(-retentionDays.Value) - SecurityAuditVerification.PurgeLag;
        var latest = now + SecurityAuditVerification.ClockSkew;

        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        var last = await LastCheckpointAsync(context, cancellationToken);
        var sealedFrom = last.SealedFrom;
        var lastId = last.MaxAuditId;
        var seen = new List<SecurityAuditVerificationService.SeenEvent>();
        var failed = new List<int>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        while (seen.Count < MaxRowsPerRun)
        {
            // By id alone: a row is never passed over for the time it carries.
            var batch = await context.McpAuditLogs
                .AsNoTracking()
                .Where(x => x.Id > lastId)
                .OrderBy(x => x.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            var firstId = batch[0].Id;
            var lastBatchId = batch[^1].Id;
            var statements = (await context.McpAuditStatements
                    .AsNoTracking()
                    .Where(x => x.McpAuditLogId >= firstId)
                    .Where(x => x.McpAuditLogId <= lastBatchId)
                    .ToListAsync(cancellationToken))
                .ToLookup(x => x.McpAuditLogId);

            foreach (var row in batch)
            {
                var verdict = McpAuditSeal.Verify(row, statements[row.Id].ToList(), integrity);
                if (verdict == McpAuditVerdicts.Unsealed && sealedFrom is { } boundary && row.Id > boundary.Id && now > boundary.Time + UpgradeGrace)
                {
                    verdict = SecurityAuditVerdicts.Modified;
                }

                if (verdict == SecurityAuditVerdicts.Intact && (row.CreatedTime < earliest || row.CreatedTime > latest))
                {
                    verdict = SecurityAuditVerdicts.OutOfRange;
                }

                if (verdict == SecurityAuditVerdicts.Intact)
                {
                    sealedFrom ??= (row.Id, row.CreatedTime);
                }

                counts[verdict] = counts.GetValueOrDefault(verdict) + 1;
                if (verdict != SecurityAuditVerdicts.Intact && verdict != McpAuditVerdicts.Unsealed)
                {
                    failed.Add(row.Id);
                }

                seen.Add(new SecurityAuditVerificationService.SeenEvent(row.Id, row.Hmac ?? string.Empty, verdict == SecurityAuditVerdicts.Intact ? row.CreatedTime : null));
            }

            lastId = lastBatchId;
        }

        var advanced = SecurityAuditVerificationService.Advance(last.MaxAuditId, seen, now - SecurityAuditVerificationService.GapGrace);
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
                .Take(SecurityAuditVerificationService.MaxReportedEvents)
                .ToList())
        {
            SealedFromAuditId = sealedFrom?.Id,
            NeverCommittedCount = advanced.NeverCommittedCount,
            WaitingForAuditId = advanced.WaitingForEventId
        };

        return (result, sealedFrom?.Time);
    }

    // The checkpoint the latest intact MCP audit verification recorded: the id it reached and the first intact sealed row
    // it knew. A checkpoint event that does not verify is reported and passed over: a forged one cannot make a run skip rows.
    private async Task<LastCheckpoint> LastCheckpointAsync(BeaconContext context, CancellationToken cancellationToken)
    {
        var recent = await context.SecurityAuditEvents
            .AsNoTracking()
            .Where(x => x.Action == SecurityAuditActions.McpAuditVerified)
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
                    SecurityAuditLogEvents.McpAuditVerificationFailed,
                    "MCP audit verification checkpoint {AuditId} fails verification and is passed over",
                    auditEvent.Id);
                BeaconTelemetry.SecurityAuditVerificationFailures.Add(1, new KeyValuePair<string, object?>(BeaconTelemetry.SecurityAuditActionTag, SecurityAuditActions.McpAuditVerified));

                continue;
            }

            var details = string.IsNullOrEmpty(auditEvent.DetailsJson) ? null : JsonNode.Parse(auditEvent.DetailsJson);
            if (IntOf(details, MaxAuditIdDetail) is not { } maxAuditId)
            {
                continue;
            }

            var sealedFrom = IntOf(details, SealedFromAuditIdDetail) is { } sealedFromId && TimeOf(details, SealedFromTimeDetail) is { } sealedFromTime
                ? (sealedFromId, sealedFromTime)
                : ((int Id, DateTime Time)?)null;

            return new LastCheckpoint(maxAuditId, sealedFrom);
        }

        return new LastCheckpoint(0, null);
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

    private async Task ReportAsync(McpAuditCheckpoint checkpoint, DateTime? sealedFromTime, CancellationToken cancellationToken)
    {
        var failedCount = checkpoint.ModifiedCount + checkpoint.UnknownKeyCount + checkpoint.OutOfRangeCount;
        if (failedCount > 0)
        {
            _auditLogger.LogError(
                SecurityAuditLogEvents.McpAuditVerificationFailed,
                "MCP audit verification found {FailedCount} rows that fail (modified {ModifiedCount}, unknown key {UnknownKeyCount}, out of range {OutOfRangeCount}) after row {FromAuditId}; first ids {FailedAuditIds}",
                failedCount,
                checkpoint.ModifiedCount,
                checkpoint.UnknownKeyCount,
                checkpoint.OutOfRangeCount,
                checkpoint.FromAuditId,
                string.Join(",", checkpoint.FailedAuditIds));
            BeaconTelemetry.SecurityAuditVerificationFailures.Add(
                failedCount,
                new KeyValuePair<string, object?>(BeaconTelemetry.SecurityAuditActionTag, SecurityAuditActions.McpAuditVerified));
        }

        _auditLogger.Log(
            LogLevel.Information,
            SecurityAuditLogEvents.McpAuditCheckpoint,
            "MCP audit checkpoint: FromAuditId={FromAuditId} MaxAuditId={MaxAuditId} Checked={Checked} Failed={Failed} Unsealed={Unsealed} LastHmac={LastHmac} KeyId={KeyId} SealedFromAuditId={SealedFromAuditId} NeverCommitted={NeverCommittedCount} WaitingForAuditId={WaitingForAuditId}",
            checkpoint.FromAuditId,
            checkpoint.MaxAuditId,
            checkpoint.CheckedCount,
            failedCount,
            checkpoint.UnsealedCount,
            checkpoint.LastHmac,
            integrity.CurrentKeyId,
            checkpoint.SealedFromAuditId,
            checkpoint.NeverCommittedCount,
            checkpoint.WaitingForAuditId);

        if (checkpoint.CheckedCount == 0)
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
                    .Add("modifiedCount", checkpoint.ModifiedCount)
                    .Add("unknownKeyCount", checkpoint.UnknownKeyCount)
                    .Add("outOfRangeCount", checkpoint.OutOfRangeCount)
                    .Add("unsealedCount", checkpoint.UnsealedCount)
                    .Add("failedAuditIds", checkpoint.FailedAuditIds)
                    .Add(SealedFromAuditIdDetail, checkpoint.SealedFromAuditId)
                    .Add(SealedFromTimeDetail, sealedFromTime)
                    .Add("neverCommittedCount", checkpoint.NeverCommittedCount)
                    .Add("waitingForAuditId", checkpoint.WaitingForAuditId)),
            cancellationToken);
    }

    private sealed record LastCheckpoint(int MaxAuditId, (int Id, DateTime Time)? SealedFrom);
}
