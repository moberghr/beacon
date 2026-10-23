using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Base;

namespace Beacon.Core.Services.Retention;

/// <summary>
/// Clears the content of the content stores' rows (<see cref="McpRetentionDenyList.ContentStores"/>) in place, keeping the
/// rows and their structure: every property the registry classifies as content or error text becomes <c>null</c> (or
/// <c>""</c> where the column is required). Rows are read in batches of ids that still hold content, so a cleared row is
/// never rewritten.
/// </summary>
internal static class ContentExpiry
{
    internal static readonly string[] ResultSnapshotProperties = [nameof(QueryExecutionHistory.Results)];

    private static readonly ContentStore[] Stores =
    [
        Store(x => x.QueryExecutionHistory),
        Store(x => x.Notifications),
        Store(x => x.ManualQueryExecutionLogs),
        Store(x => x.AiActorExecutions),
        Store(x => x.AiActorConversations),
        Store(x => x.MigrationExecutions)
    ];

    /// <summary>The stores the content expiry covers, by entity type; the registry's <c>ContentStores</c> must match.</summary>
    internal static IReadOnlyList<Type> StoreTypes { get; } = Stores
        .Select(x => x.Entity)
        .ToList();

    /// <summary>Clears every content property of the rows created before <paramref name="cutoff"/>; returns the rows cleared per store.</summary>
    internal static async Task<IReadOnlyDictionary<string, int>> ClearAsync(BeaconContext context, DateTime cutoff, CancellationToken cancellationToken)
    {
        var cleared = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var store in Stores)
        {
            cleared[store.Entity.Name] = await store.Clear(context, cutoff, null, cancellationToken);
        }

        return cleared;
    }

    /// <summary>Clears the stored result snapshots (run history and notifications) of every age: the content lock is on.</summary>
    internal static async Task<int> ClearResultSnapshotsAsync(BeaconContext context, DateTime now, CancellationToken cancellationToken)
    {
        var runs = await ClearAsync(context.QueryExecutionHistory, now, ResultSnapshotProperties, cancellationToken);
        var notifications = await ClearAsync(context.Notifications, now, ResultSnapshotProperties, cancellationToken);

        return runs + notifications;
    }

    /// <summary>The content properties of one store: its registry rules that are not structural.</summary>
    internal static IReadOnlyList<string> ContentProperties(Type entity) =>
        McpRetentionDenyList.RulesFor(entity)
            .Where(x => x.Kind != RetentionKind.Structural)
            .Select(x => x.Property)
            .ToList();

    /// <summary>The rows the expiry clears: created before the cutoff and still holding content in one of the properties.</summary>
    internal static IQueryable<T> Expired<T>(IQueryable<T> source, DateTime cutoff, IReadOnlyList<string> properties)
        where T : BaseEntity
    {
        return source
            .Where(x => x.CreatedTime < cutoff)
            .Where(HoldsContent<T>(properties));
    }

    private static async Task<int> ClearAsync<T>(DbSet<T> set, DateTime cutoff, IReadOnlyList<string>? only, CancellationToken cancellationToken)
        where T : BaseEntity
    {
        var properties = only ?? ContentProperties(typeof(T));
        var cleared = 0;
        while (true)
        {
            var ids = await Expired(set.AsNoTracking(), cutoff, properties)
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .Take(RetentionBatches.BatchSize)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
            {
                return cleared;
            }

            var batch = await set
                .Where(x => ids.Contains(x.Id))
                .ExecuteUpdateAsync(x => SetCleared(x, properties), cancellationToken);
            if (batch == 0)
            {
                return cleared;
            }

            cleared += batch;
        }
    }

    private static void SetCleared<T>(UpdateSettersBuilder<T> setters, IReadOnlyList<string> properties)
        where T : BaseEntity
    {
        foreach (var property in properties)
        {
            var cleared = McpRetentionDenyList.IsRequiredString(typeof(T), property) ? string.Empty : null;
            setters.SetProperty(x => EF.Property<string?>(x, property), cleared);
        }
    }

    // x => (x.A != null && x.A != "") || (x.B != null && x.B != "") || ...
    private static Expression<Func<T, bool>> HoldsContent<T>(IReadOnlyList<string> properties)
    {
        var row = Expression.Parameter(typeof(T), "x");
        Expression? any = null;
        foreach (var property in properties)
        {
            var value = Expression.Property(row, property);
            var holds = Expression.AndAlso(
                Expression.NotEqual(value, Expression.Constant(null, typeof(string))),
                Expression.NotEqual(value, Expression.Constant(string.Empty)));
            any = any == null ? holds : Expression.OrElse(any, holds);
        }

        return Expression.Lambda<Func<T, bool>>(any ?? Expression.Constant(false), row);
    }

    private static ContentStore Store<T>(Func<BeaconContext, DbSet<T>> set)
        where T : BaseEntity
    {
        return new ContentStore(typeof(T), (context, cutoff, only, cancellationToken) => ClearAsync(set(context), cutoff, only, cancellationToken));
    }

    private sealed record ContentStore(Type Entity, Func<BeaconContext, DateTime, IReadOnlyList<string>?, CancellationToken, Task<int>> Clear);
}
