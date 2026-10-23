using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Helpers;
using Beacon.Core.Telemetry;

namespace Beacon.Core.Services.Retention;

/// <summary>
/// The data retention job (<c>Beacon:Retention</c>): deletes the rows of every registered <see cref="IRetentionTarget"/>
/// older than its window, then clears the content of the content stores' rows older than <c>ContentDays</c> and, while the
/// content lock is on, every stored result snapshot. Each target runs on its own context and its failure is logged and
/// counted without stopping the others; the run then throws, so the scheduled job fails visibly. Logs counts and names
/// only. The security audit trail is never a target: only its own verified purge deletes events. The sample host
/// schedules this as the Warp recurring job <c>data-retention</c>; other hosts schedule it themselves.
/// </summary>
public interface IDataRetentionService
{
    Task<DataRetentionRun> RunAsync(CancellationToken cancellationToken);
}

/// <summary>What one run did: rows deleted per target, and rows whose content was cleared.</summary>
public sealed record DataRetentionRun(IReadOnlyDictionary<string, int> Deleted, int ContentCleared);

internal sealed class DataRetentionService(
    IDbContextFactory<BeaconContext> contextFactory,
    IEnumerable<IRetentionTarget> targets,
    IOptions<RetentionOptions> options,
    IContentRetentionPolicy contentRetentionPolicy,
    TimeProvider timeProvider,
    ILoggerFactory loggerFactory) : IDataRetentionService
{
    internal const string ContentStep = "content";

    internal static readonly EventId PurgedEvent = new(9120, "DataRetentionPurged");
    internal static readonly EventId FailedEvent = new(9121, "DataRetentionTargetFailed");
    internal static readonly EventId ContentClearedEvent = new(9122, "DataRetentionContentCleared");

    private readonly ILogger _auditLogger = loggerFactory.CreateLogger(BeaconTelemetry.AuditLogCategory);

    public async Task<DataRetentionRun> RunAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var deleted = new Dictionary<string, int>(StringComparer.Ordinal);
        var failed = new List<string>();

        foreach (var target in targets)
        {
            var days = settings.DaysFor(target.Name);
            if (days == 0)
            {
                _auditLogger.LogDebug("Data retention skipped {Target}: its rows are kept forever.", target.Name);

                continue;
            }

            var cutoff = now.AddDays(-days);
            try
            {
                await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
                deleted[target.Name] = await target.DeleteExpiredAsync(context, cutoff, cancellationToken);
                _auditLogger.Log(
                    LogLevel.Information,
                    PurgedEvent,
                    "Data retention deleted {Deleted} {Target} rows older than {Cutoff} (retention {RetentionDays} days)",
                    deleted[target.Name],
                    target.Name,
                    cutoff,
                    days);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                failed.Add(target.Name);
                _auditLogger.LogError(FailedEvent, "Data retention of {Target} did not run ({Failure})", target.Name, FailureCodes.Describe(ex));
            }
        }

        var contentCleared = await ClearContentAsync(settings, now, failed, cancellationToken);

        if (failed.Count > 0)
        {
            throw new InvalidOperationException(
                $"Data retention did not complete for: {string.Join(", ", failed)}. The other targets ran; see the log for each failure.");
        }

        return new DataRetentionRun(deleted, contentCleared);
    }

    private async Task<int> ClearContentAsync(RetentionOptions settings, DateTime now, List<string> failed, CancellationToken cancellationToken)
    {
        try
        {
            var cleared = 0;
            await using var context = await contextFactory.CreateDbContextAsync(cancellationToken);
            if (settings.ContentDays > 0)
            {
                var cutoff = now.AddDays(-settings.ContentDays);
                var perStore = await ContentExpiry.ClearAsync(context, cutoff, cancellationToken);
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
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            failed.Add(ContentStep);
            _auditLogger.LogError(FailedEvent, "Data retention of {Target} did not run ({Failure})", ContentStep, FailureCodes.Describe(ex));

            return 0;
        }
    }
}
