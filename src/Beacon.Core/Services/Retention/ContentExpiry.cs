using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Logging;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Base;
using Beacon.Core.SecurityAudit;

namespace Beacon.Core.Services.Retention;

/// <summary>
/// Clears the content of the content stores' rows (<see cref="McpRetentionDenyList.ContentStores"/>) in place, keeping the
/// rows and their structure: every property the registry classifies as content becomes <c>null</c> (or <c>""</c> where
/// the column is required), and error text becomes its error class, as the write-time lock writes it. Rows are read in
/// batches of ids past the last batch that still hold content (an error already reduced to its class holds none), so a
/// cleared row is never read or rewritten again. A cancelled token between two batches ends a store with the count so far.
/// The execution log is sealed (<see cref="AuditTableSeal"/>): each of its rows is cleared on its own, and a row whose seal
/// verifies intact is sealed again over what is left of it, in the same statement, which applies only while the row still
/// carries the seal that was verified. A row whose seal does not verify is cleared without a new seal, so it keeps failing
/// verification; a row without a seal stays without one. A row whose seal this node cannot read (a later seal version, or a
/// key it does not hold) is left whole for a node that can. While the table lacks its seal columns (the seal migration not
/// applied, <see cref="AuditTableSealSchema"/>) it is cleared like any other store.
/// </summary>
internal static class ContentExpiry
{
    internal static readonly string[] ResultSnapshotProperties = [nameof(QueryExecutionHistory.Results)];

    private static readonly string[] ErrorClassValues = McpContentRedactor.ErrorClasses
        .Order(StringComparer.Ordinal)
        .ToArray();

    private static readonly ContentStore[] Stores =
    [
        Store(x => x.QueryExecutionHistory),
        Store(x => x.Notifications),
        SealedStore(x => x.ManualQueryExecutionLogs, AuditTableStores.ExecutionLog),
        Store(x => x.AiActorExecutions),
        Store(x => x.AiActorConversations),
        Store(x => x.MigrationExecutions)
    ];

    /// <summary>The stores the content expiry covers, by entity type; the registry's <c>ContentStores</c> must match.</summary>
    internal static IReadOnlyList<Type> StoreTypes { get; } = Stores
        .Select(x => x.Entity)
        .ToList();

    /// <summary>
    /// Clears every content property of the rows created before <paramref name="cutoff"/>; returns the rows cleared per
    /// store. Sealed rows are sealed again under <paramref name="integrity"/>; without it (no seal to keep) they are cleared
    /// like any other store's.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<string, int>> ClearAsync(
        BeaconContext context,
        DateTime cutoff,
        CancellationToken cancellationToken,
        SecurityAuditIntegrity? integrity = null,
        AuditTableSealSchema? sealSchema = null,
        ILogger? logger = null)
    {
        var cleared = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var store in Stores)
        {
            // A sealed table without its seal columns (the migration not applied) holds no seal to keep.
            var sealing = integrity != null && (store.Sealed == false || sealSchema == null || !await sealSchema.SealColumnsMissingAsync(context, store.Entity, cancellationToken))
                ? integrity
                : null;
            cleared[store.Entity.Name] = await store.Clear(context, cutoff, sealing, logger, cancellationToken);
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

    /// <summary>
    /// The rows the expiry clears: created before the cutoff and still holding content, a non-empty value in a content
    /// property or error text that is not yet an error class.
    /// </summary>
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
        var content = properties
            .Where(x => !IsErrorClass(typeof(T), x))
            .ToList();
        var errors = properties
            .Where(x => IsErrorClass(typeof(T), x))
            .ToList();
        var cleared = 0;
        var after = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var ids = await Expired(set.AsNoTracking(), cutoff, properties)
                .Where(x => x.Id > after)
                .OrderBy(x => x.Id)
                .Select(x => x.Id)
                .Take(RetentionBatches.BatchSize)
                .ToListAsync(cancellationToken);
            if (ids.Count == 0)
            {
                return cleared;
            }

            after = ids[^1];
            if (content.Count > 0)
            {
                await set
                    .Where(x => ids.Contains(x.Id))
                    .ExecuteUpdateAsync(x => SetCleared(x, content), cancellationToken);
            }

            foreach (var property in errors)
            {
                await ReplaceWithErrorClassAsync(set, ids, property, cancellationToken);
            }

            cleared += ids.Count;
            if (ids.Count < RetentionBatches.BatchSize)
            {
                return cleared;
            }
        }

        return cleared;
    }

    // One row at a time: read whole (its seal is verified over every field), cleared in process as the batch update clears
    // it, sealed again when its seal verified, then written by one update that applies only while the row still carries the
    // seal it was read with. The text read here never leaves this method.
    internal static async Task<int> ClearSealedAsync<T>(
        DbSet<T> set,
        AuditTableStore<T> store,
        DateTime cutoff,
        SecurityAuditIntegrity integrity,
        CancellationToken cancellationToken,
        ILogger? logger = null)
        where T : BaseEntity, ISealedAuditRow
    {
        var properties = ContentProperties(typeof(T));
        var cleared = 0;
        var after = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var rows = await Expired(set.AsNoTracking(), cutoff, properties)
                .Where(x => x.Id > after)
                .OrderBy(x => x.Id)
                .Take(RetentionBatches.BatchSize)
                .ToListAsync(cancellationToken);
            if (rows.Count == 0)
            {
                return cleared;
            }

            after = rows[^1].Id;
            var left = 0;
            foreach (var row in rows)
            {
                var (count, unreadable) = await ClearSealedRowAsync(set, store, row, properties, integrity, cancellationToken);
                cleared += count;
                left += unreadable ? 1 : 0;
            }

            if (left > 0)
            {
                logger?.LogInformation(
                    "Content expiry left {Left} {Store} rows whose seal this node cannot verify (a later seal version or a key it does not hold): a node that can verify them clears them",
                    left,
                    store.Name);
            }

            if (rows.Count < RetentionBatches.BatchSize)
            {
                return cleared;
            }
        }

        return cleared;
    }

    private static async Task<(int Cleared, bool Unreadable)> ClearSealedRowAsync<T>(
        DbSet<T> set,
        AuditTableStore<T> store,
        T row,
        IReadOnlyList<string> properties,
        SecurityAuditIntegrity integrity,
        CancellationToken cancellationToken)
        where T : BaseEntity, ISealedAuditRow
    {
        var verdict = AuditTableSeal.Verify(row, store, integrity);
        if (verdict is McpAuditVerdicts.UnknownVersion or SecurityAuditVerdicts.UnknownKey)
        {
            // A seal this node cannot read (a later seal version, or a key it does not hold) is never destroyed: the row
            // is left for a node that can verify it, and cleared then.
            return (0, true);
        }

        var intact = verdict == SecurityAuditVerdicts.Intact;
        var readHmac = row.Hmac;
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var property in properties)
        {
            var info = typeof(T).GetProperty(property)!;
            var value = IsErrorClass(typeof(T), property)
                ? McpContentRedactor.ErrorClassOf(info.GetValue(row) as string)
                : McpRetentionDenyList.IsRequiredString(typeof(T), property) ? string.Empty : null;
            info.SetValue(row, value);
            values[property] = value;
        }

        if (intact)
        {
            AuditTableSeal.Seal(row, store, integrity);
        }

        var updated = await set
            .Where(x => x.Id == row.Id)
            .Where(x => EF.Property<string?>(x, nameof(ISealedAuditRow.Hmac)) == readHmac)
            .ExecuteUpdateAsync(
                x =>
                {
                    foreach (var (property, value) in values)
                    {
                        x.SetProperty(y => EF.Property<string?>(y, property), value);
                    }

                    if (intact)
                    {
                        x.SetProperty(y => EF.Property<string?>(y, nameof(ISealedAuditRow.Hmac)), row.Hmac);
                        x.SetProperty(y => EF.Property<string?>(y, nameof(ISealedAuditRow.HmacKeyId)), row.HmacKeyId);
                        x.SetProperty(y => EF.Property<int?>(y, nameof(ISealedAuditRow.SealVersion)), row.SealVersion);
                    }
                },
                cancellationToken);

        return (updated, false);
    }

    // The class is computed in process (McpContentRedactor.ErrorClassOf, as the interceptor does), then written with one
    // update per class. The error text read here never leaves this method.
    private static async Task ReplaceWithErrorClassAsync<T>(DbSet<T> set, List<int> ids, string property, CancellationToken cancellationToken)
        where T : BaseEntity
    {
        var rows = await set
            .AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .Where(x => EF.Property<string?>(x, property) != null)
            .Select(x =>
                new
                {
                    x.Id,
                    Value = EF.Property<string?>(x, property)
                })
            .ToListAsync(cancellationToken);
        var byClass = rows
            .Select(x => (x.Id, x.Value, Class: McpContentRedactor.ErrorClassOf(x.Value)))
            .Where(x => !string.Equals(x.Value, x.Class, StringComparison.Ordinal))
            .GroupBy(x => x.Class);
        foreach (var group in byClass)
        {
            var classIds = group
                .Select(x => x.Id)
                .ToList();
            await set
                .Where(x => classIds.Contains(x.Id))
                .ExecuteUpdateAsync(x => x.SetProperty(y => EF.Property<string?>(y, property), group.Key), cancellationToken);
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

    private static bool IsErrorClass(Type entity, string property) =>
        McpRetentionDenyList.RulesFor(entity)
            .Any(x => x.Property == property && x.Kind == RetentionKind.ErrorClass);

    // x => (x.A != null && x.A != "") || (x.E != null && x.E != "" && !classes.Contains(x.E)) || ...
    private static Expression<Func<T, bool>> HoldsContent<T>(IReadOnlyList<string> properties)
    {
        var row = Expression.Parameter(typeof(T), "x");
        Expression? any = null;
        foreach (var property in properties)
        {
            var value = Expression.Property(row, property);
            Expression holds = Expression.AndAlso(
                Expression.NotEqual(value, Expression.Constant(null, typeof(string))),
                Expression.NotEqual(value, Expression.Constant(string.Empty)));
            if (IsErrorClass(typeof(T), property))
            {
                var isClass = Expression.Call(
                    typeof(Enumerable),
                    nameof(Enumerable.Contains),
                    [typeof(string)],
                    Expression.Constant(ErrorClassValues),
                    value);
                holds = Expression.AndAlso(holds, Expression.Not(isClass));
            }

            any = any == null ? holds : Expression.OrElse(any, holds);
        }

        return Expression.Lambda<Func<T, bool>>(any ?? Expression.Constant(false), row);
    }

    private static ContentStore Store<T>(Func<BeaconContext, DbSet<T>> set)
        where T : BaseEntity
    {
        return new ContentStore(typeof(T), false, (context, cutoff, _, _, cancellationToken) => ClearAsync(set(context), cutoff, null, cancellationToken));
    }

    private static ContentStore SealedStore<T>(Func<BeaconContext, DbSet<T>> set, AuditTableStore<T> store)
        where T : BaseEntity, ISealedAuditRow
    {
        return new ContentStore(
            typeof(T),
            true,
            (context, cutoff, integrity, logger, cancellationToken) => integrity == null
                ? ClearAsync(set(context), cutoff, null, cancellationToken)
                : ClearSealedAsync(set(context), store, cutoff, integrity, cancellationToken, logger));
    }

    private sealed record ContentStore(Type Entity, bool Sealed, Func<BeaconContext, DateTime, SecurityAuditIntegrity?, ILogger?, CancellationToken, Task<int>> Clear);
}
