using Microsoft.EntityFrameworkCore;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Base;
using Beacon.Core.Data.Entities.DataMigration;
using Beacon.Core.Data.Enums;

namespace Beacon.Core.Services.Retention;

/// <summary>The names of Beacon's own retention targets, as <c>Beacon:Retention:Targets</c> keys.</summary>
public static class RetentionTargetNames
{
    public const string SubscriptionRuns = "subscription-runs";
    public const string Notifications = "notifications";
    public const string AnomalyBaselines = "anomaly-baselines";
    public const string AiActorExecutions = "ai-actor-executions";
    public const string MigrationRuns = "migration-runs";
    public const string McpEvalRuns = "mcp-eval-runs";
}

/// <summary>
/// Deletes in batches of ids: each batch is read in id order, then deleted by those ids with the expiry predicate applied
/// again, so a row that stopped qualifying meanwhile (newly referenced) stays.
/// </summary>
internal static class RetentionBatches
{
    internal const int BatchSize = 1000;

    internal static async Task<int> DeleteAsync<T>(
        DbSet<T> set,
        Func<IQueryable<T>, IQueryable<T>> expired,
        Func<List<int>, Task>? beforeDelete,
        CancellationToken cancellationToken)
        where T : BaseEntity
    {
        var deleted = 0;
        while (true)
        {
            var ids = await expired(set.AsNoTracking())
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .Take(BatchSize)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
            {
                return deleted;
            }

            if (beforeDelete != null)
            {
                await beforeDelete(ids);
            }

            var batch = await expired(set)
                .Where(x => ids.Contains(x.Id))
                .ExecuteDeleteAsync(cancellationToken);
            if (batch == 0)
            {
                return deleted;
            }

            deleted += batch;
        }
    }
}

/// <summary>Subscription runs (<c>QueryExecutionHistory</c>) with their notifications.</summary>
internal sealed class SubscriptionRunsRetentionTarget : IRetentionTarget
{
    public string Name => RetentionTargetNames.SubscriptionRuns;

    public Task<int> DeleteExpiredAsync(BeaconContext context, DateTime cutoff, CancellationToken cancellationToken)
    {
        return RetentionBatches.DeleteAsync(
            context.QueryExecutionHistory,
            x => Expired(x, cutoff),
            ids => context.Notifications
                .Where(x => ids.Contains(x.QueryExecutionHistoryId))
                .ExecuteDeleteAsync(cancellationToken),
            cancellationToken);
    }

    internal static IQueryable<QueryExecutionHistory> Expired(IQueryable<QueryExecutionHistory> source, DateTime cutoff) =>
        source.Where(x => x.CreatedTime < cutoff);
}

/// <summary>Delivered notifications (a copy per recipient of a run), kept no longer than their own window.</summary>
internal sealed class NotificationsRetentionTarget : IRetentionTarget
{
    public string Name => RetentionTargetNames.Notifications;

    public Task<int> DeleteExpiredAsync(BeaconContext context, DateTime cutoff, CancellationToken cancellationToken)
    {
        return RetentionBatches.DeleteAsync(context.Notifications, x => Expired(x, cutoff), null, cancellationToken);
    }

    internal static IQueryable<Notification> Expired(IQueryable<Notification> source, DateTime cutoff) =>
        source.Where(x => x.CreatedTime < cutoff);
}

/// <summary>
/// Anomaly baselines. The cutoff moves back by the longest configured look-back, so every subscription keeps the points its
/// detection and its chart read.
/// </summary>
internal sealed class AnomalyBaselinesRetentionTarget : IRetentionTarget
{
    public string Name => RetentionTargetNames.AnomalyBaselines;

    public async Task<int> DeleteExpiredAsync(BeaconContext context, DateTime cutoff, CancellationToken cancellationToken)
    {
        var longestLookbackDays = await context.AnomalyConfigs
            .MaxAsync(x => (int?)x.LookbackDays, cancellationToken) ?? 0;
        var effectiveCutoff = cutoff.AddDays(-Math.Clamp(longestLookbackDays, 0, Configuration.RetentionOptions.MaxDays));

        return await RetentionBatches.DeleteAsync(context.AnomalyBaselines, x => Expired(x, effectiveCutoff), null, cancellationToken);
    }

    internal static IQueryable<AnomalyBaseline> Expired(IQueryable<AnomalyBaseline> source, DateTime cutoff) =>
        source.Where(x => x.CreatedTime < cutoff);
}

/// <summary>
/// AI actor conversation turns and executions. An execution a query change still names (its change history) or a newer
/// turn still references is kept.
/// </summary>
internal sealed class AiActorExecutionsRetentionTarget : IRetentionTarget
{
    public string Name => RetentionTargetNames.AiActorExecutions;

    public async Task<int> DeleteExpiredAsync(BeaconContext context, DateTime cutoff, CancellationToken cancellationToken)
    {
        var turns = await RetentionBatches.DeleteAsync(context.AiActorConversations, x => ExpiredTurns(x, cutoff), null, cancellationToken);
        var executions = await RetentionBatches.DeleteAsync(context.AiActorExecutions, x => ExpiredExecutions(context, x, cutoff), null, cancellationToken);

        return turns + executions;
    }

    internal static IQueryable<AiActorConversation> ExpiredTurns(IQueryable<AiActorConversation> source, DateTime cutoff) =>
        source.Where(x => x.CreatedTime < cutoff);

    internal static IQueryable<AiActorExecution> ExpiredExecutions(BeaconContext context, IQueryable<AiActorExecution> source, DateTime cutoff) =>
        source
            .Where(x => x.CreatedTime < cutoff)
            .Where(x => !context.QueryStepChangeHistory.Any(y => y.AiActorExecutionId == x.Id))
            .Where(x => !context.AiActorConversations.Any(y => y.AiActorExecutionId == x.Id));
}

/// <summary>Data migration runs that finished; a run a retry still names as its parent is kept until the retry goes.</summary>
internal sealed class MigrationRunsRetentionTarget : IRetentionTarget
{
    public string Name => RetentionTargetNames.MigrationRuns;

    public Task<int> DeleteExpiredAsync(BeaconContext context, DateTime cutoff, CancellationToken cancellationToken)
    {
        return RetentionBatches.DeleteAsync(context.MigrationExecutions, x => Expired(context, x, cutoff), null, cancellationToken);
    }

    internal static IQueryable<MigrationExecutionHistory> Expired(BeaconContext context, IQueryable<MigrationExecutionHistory> source, DateTime cutoff) =>
        source
            .Where(x => x.CreatedTime < cutoff)
            .Where(x => x.Status != MigrationStatus.Queued)
            .Where(x => x.Status != MigrationStatus.Running)
            .Where(x => !context.MigrationExecutions.Any(y => y.ParentExecutionId == x.Id));
}

/// <summary>MCP eval runs that finished, with their per-case results. The golden cases themselves are never purged.</summary>
internal sealed class McpEvalRunsRetentionTarget : IRetentionTarget
{
    internal const string RunningStatus = "Running";

    public string Name => RetentionTargetNames.McpEvalRuns;

    public Task<int> DeleteExpiredAsync(BeaconContext context, DateTime cutoff, CancellationToken cancellationToken)
    {
        return RetentionBatches.DeleteAsync(
            context.McpEvalRuns,
            x => Expired(x, cutoff),
            ids => context.McpEvalResults
                .Where(x => ids.Contains(x.EvalRunId))
                .ExecuteDeleteAsync(cancellationToken),
            cancellationToken);
    }

    internal static IQueryable<McpEvalRun> Expired(IQueryable<McpEvalRun> source, DateTime cutoff) =>
        source
            .Where(x => x.CreatedTime < cutoff)
            .Where(x => x.Status != RunningStatus);
}
