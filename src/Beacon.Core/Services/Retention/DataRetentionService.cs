using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Beacon.Core.Telemetry;

namespace Beacon.Core.Services.Retention;

/// <summary>
/// The data retention job (<c>Beacon:Retention</c>): first clears the content of the content stores' rows older than
/// <c>ContentDays</c> and, while the content lock is on, every stored result snapshot; then deletes the rows of every
/// registered <see cref="IRetentionTarget"/> older than its window. The run works within <see cref="RetentionOptions.RunBudgetMinutes"/>: each step
/// gets an equal share of the time left, and a step whose share runs out stops there (logged, the run still succeeds) and
/// continues on the next run, so one large backlog never starves the steps after it. Each step runs on its own context and
/// its failure is logged and counted without stopping the others; the run then throws, so the scheduled job fails
/// visibly. Logs counts and names only. The security audit trail is never a target: only its own verified purge deletes
/// events. The sample host schedules this as the Warp recurring job <c>data-retention</c>; other hosts schedule it
/// themselves, with a timeout longer than <see cref="RetentionOptions.RunBudgetMinutes"/>.
/// </summary>
public interface IDataRetentionService
{
    Task<DataRetentionRun> RunAsync(CancellationToken cancellationToken);
}

/// <summary>
/// What one run did: rows deleted per target, rows whose content was cleared, and the steps (target names, or
/// <c>content</c>) whose share of the run's time ran out before they finished; the next run continues them.
/// </summary>
public sealed record DataRetentionRun(IReadOnlyDictionary<string, int> Deleted, int ContentCleared, IReadOnlyList<string> Incomplete);

internal sealed class DataRetentionService(
    IDbContextFactory<BeaconContext> contextFactory,
    IEnumerable<IRetentionTarget> targets,
    IOptions<RetentionOptions> options,
    IContentRetentionPolicy contentRetentionPolicy,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory,
    SecurityAudit.SecurityAuditIntegrity? integrity = null,
    SecurityAudit.AuditTableSealSchema? sealSchema = null) : IDataRetentionService
{
    internal const string ContentStep = "content";

    internal static readonly EventId PurgedEvent = new(9120, "DataRetentionPurged");
    internal static readonly EventId FailedEvent = new(9121, "DataRetentionTargetFailed");
    internal static readonly EventId ContentClearedEvent = new(9122, "DataRetentionContentCleared");
    internal static readonly EventId BudgetSpentEvent = new(9124, "DataRetentionBudgetSpent");

    private readonly ILogger _auditLogger = loggerFactory.CreateLogger(BeaconTelemetry.AuditLogCategory);

    public async Task<DataRetentionRun> RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var started = timeProvider.GetUtcNow();
        var deadline = started + TimeSpan.FromMinutes(RetentionOptions.RunBudgetMinutes);
        var now = started.UtcDateTime;
        var failed = new List<string>();
        var incomplete = new List<string>();

        var active = new List<IRetentionTarget>();
        foreach (var target in targets)
        {
            if (settings.DaysFor(target.Name) == 0)
            {
                _auditLogger.LogDebug("Data retention skipped {Target}: its rows are kept forever.", target.Name);

                continue;
            }

            active.Add(target);
        }

        var stepsLeft = active.Count + 1;
        var contentCleared = await RunStepAsync(
            ContentStep,
            ShareOf(deadline, stepsLeft--),
            token => ClearContentAsync(settings, now, token),
            failed,
            incomplete,
            cancellationToken);

        var deleted = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var target in active)
        {
            var days = settings.DaysFor(target.Name);
            var cutoff = now.AddDays(-days);
            deleted[target.Name] = await RunStepAsync(
                target.Name,
                ShareOf(deadline, stepsLeft--),
                async token =>
                {
                    await using var context = await contextFactory.CreateDbContextAsync(token);
                    var count = await target.DeleteExpiredAsync(context, cutoff, token);
                    _auditLogger.Log(
                        LogLevel.Information,
                        PurgedEvent,
                        "Data retention deleted {Deleted} {Target} rows older than {Cutoff} (retention {RetentionDays} days)",
                        count,
                        target.Name,
                        cutoff,
                        days);

                    return count;
                },
                failed,
                incomplete,
                cancellationToken);
        }

        if (failed.Count > 0)
        {
            throw new InvalidOperationException(
                $"Data retention did not complete for: {string.Join(", ", failed)}. The other targets ran; see the log for each failure.");
        }

        return new DataRetentionRun(deleted, contentCleared, incomplete);
    }

    // Runs one step within its share of the run's time. The share's token reaches every statement of the step: a step that
    // returns or stops after its share ran out is incomplete (it continues next run), not failed; the caller's own
    // cancellation ends the run.
    private async Task<int> RunStepAsync(
        string step,
        TimeSpan share,
        Func<CancellationToken, Task<int>> work,
        List<string> failed,
        List<string> incomplete,
        CancellationToken cancellationToken)
    {
        if (share <= TimeSpan.Zero)
        {
            incomplete.Add(step);
            _auditLogger.Log(LogLevel.Information, BudgetSpentEvent, "Data retention left {Target} for the next run: the run's time budget is spent", step);

            return 0;
        }

        using var budget = new CancellationTokenSource(share, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        try
        {
            var count = await work(linked.Token);
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.IsCancellationRequested)
            {
                MarkIncomplete(step, incomplete);
            }

            return count;
        }
        catch (Exception) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            MarkIncomplete(step, incomplete);

            return 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            failed.Add(step);
            _auditLogger.LogError(FailedEvent, "Data retention of {Target} did not run ({Failure})", step, FailureCodes.Describe(ex));

            return 0;
        }
    }

    private void MarkIncomplete(string step, List<string> incomplete)
    {
        incomplete.Add(step);
        _auditLogger.Log(LogLevel.Information, BudgetSpentEvent, "Data retention stopped {Target} at the end of its share of the run's time; the next run continues it", step);
    }

    private TimeSpan ShareOf(DateTimeOffset deadline, int stepsLeft)
    {
        var left = deadline - timeProvider.GetUtcNow();

        return left <= TimeSpan.Zero ? TimeSpan.Zero : left / Math.Max(stepsLeft, 1);
    }

    private async Task<int> ClearContentAsync(RetentionOptions settings, DateTime now, CancellationToken cancellationToken)
    {
        var cleared = 0;
        await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (settings.ContentDays > 0)
        {
            var cutoff = now.AddDays(-settings.ContentDays);
            var perStore = await ContentExpiry.ClearAsync(context, cutoff, cancellationToken, integrity, sealSchema, _auditLogger);
            cleared += perStore.Values.Sum();
            _auditLogger.Log(
                LogLevel.Information,
                ContentClearedEvent,
                "Data retention cleared the content of {Cleared} rows older than {Cutoff} (content retention {ContentDays} days)",
                perStore.Values.Sum(),
                cutoff,
                settings.ContentDays);
        }

        // Only a lock the settings positively report clears snapshots of every age; a settings read that fails clears
        // nothing here (the write-time lock fails closed on its own).
        var decision = await contentRetentionPolicy.ResolveAsync(null, cancellationToken);
        if (decision.Locked)
        {
            var snapshots = await ContentExpiry.ClearResultSnapshotsAsync(context, now, cancellationToken);
            cleared += snapshots;
            _auditLogger.Log(
                LogLevel.Information,
                ContentClearedEvent,
                "Data retention cleared {Cleared} stored result snapshots: the content lock is on",
                snapshots);
        }

        return cleared;
    }
}
