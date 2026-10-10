using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;
using Beacon.Core.Data;

namespace Beacon.Core.Authorization.ProjectAccess;

/// <summary>
/// The list filters (§8.2): each keeps the rows a <see cref="ProjectSet"/> reaches, inside the query the handler sends,
/// with a fixed number of subqueries whatever the number of projects. Each is a no-op (the SQL is unchanged) when the
/// set reaches every project (<see cref="ProjectSet.Ids"/> null), except that a row recorded without a project or data
/// source stays an Admin's there too. Public so list handlers in every Beacon assembly (and a host's own) filter the
/// same way.
/// </summary>
public static class ProjectAccessQueryableExtensions
{
    /// <summary>Rows whose project is in the set.</summary>
    public static IQueryable<T> WhereProjectIn<T>(this IQueryable<T> source, ProjectSet set, Expression<Func<T, int>> projectId)
    {
        if (set.Ids == null)
        {
            return source;
        }

        var ids = set.Ids.ToList();

        return source.Where(Compose(projectId, x => ids.Contains(x)));
    }

    /// <summary>
    /// Rows whose project is in the set; a row recorded without a project is an Admin's alone, also where the set reaches
    /// every project (only <see cref="ProjectSet.IsUnrestricted"/> keeps it).
    /// </summary>
    public static IQueryable<T> WhereProjectIn<T>(this IQueryable<T> source, ProjectSet set, Expression<Func<T, int?>> projectId)
    {
        if (set.Ids == null)
        {
            return set.IsUnrestricted ? source : source.Where(Compose(projectId, x => x != null));
        }

        var ids = set.Ids.ToList();

        return source.Where(Compose(projectId, x => x != null && ids.Contains(x.Value)));
    }

    /// <summary>Rows whose data source a project in the set links, or that is openly reachable when the set includes those.</summary>
    public static IQueryable<T> WhereSourceAccessible<T>(
        this IQueryable<T> source,
        BeaconContext context,
        ProjectSet set,
        Expression<Func<T, int>> dataSourceId)
    {
        return set.Ids == null ? source : source.Where(Compose(dataSourceId, SourceAccessible(context, set)));
    }

    /// <summary>
    /// As <see cref="WhereSourceAccessible{T}(IQueryable{T}, BeaconContext, ProjectSet, Expression{Func{T, int}})"/>; a
    /// row without a data source is an Admin's alone, also where the set reaches every project (only
    /// <see cref="ProjectSet.IsUnrestricted"/> keeps it).
    /// </summary>
    public static IQueryable<T> WhereSourceAccessible<T>(
        this IQueryable<T> source,
        BeaconContext context,
        ProjectSet set,
        Expression<Func<T, int?>> dataSourceId)
    {
        if (set.Ids == null)
        {
            return set.IsUnrestricted ? source : source.Where(Compose(dataSourceId, x => x != null));
        }

        var accessible = SourceAccessible(context, set);
        var value = Expression.Parameter(typeof(int?), "x");
        var predicate = Expression.Lambda<Func<int?, bool>>(
            Expression.AndAlso(
                Expression.NotEqual(value, Expression.Constant(null, typeof(int?))),
                ReplacingExpressionVisitor.Replace(accessible.Parameters[0], Expression.Property(value, nameof(Nullable<int>.Value)), accessible.Body)),
            value);

        return source.Where(Compose(dataSourceId, predicate));
    }

    /// <summary>
    /// Rows whose data sources one project in the set holds all of (a query's steps, a run's recorded sources), or that
    /// are all openly reachable when the set includes those. A row reading no data source is no project's and not open:
    /// it is kept only where the set reaches every project.
    /// </summary>
    public static IQueryable<T> WhereAllSourcesInOneProject<T>(
        this IQueryable<T> source,
        BeaconContext context,
        ProjectSet set,
        Expression<Func<T, IEnumerable<int>>> dataSourceIds)
    {
        return set.Ids == null ? source : source.Where(Compose(dataSourceIds, AllSourcesInOneProject(context, set)));
    }

    /// <summary>
    /// Rows reached through a query's steps (a query, a subscription or task on one): as
    /// <see cref="WhereAllSourcesInOneProject{T}(IQueryable{T}, BeaconContext, ProjectSet, Expression{Func{T, IEnumerable{int}}})"/>,
    /// and a row whose query has no steps is kept when the query's owner (<paramref name="queryOwnerUserId"/>) is the
    /// caller and the set reaches the queries it owns (<see cref="ProjectSet.QueryOwnerUserId"/>).
    /// </summary>
    public static IQueryable<T> WhereAllSourcesInOneProject<T>(
        this IQueryable<T> source,
        BeaconContext context,
        ProjectSet set,
        Expression<Func<T, IEnumerable<int>>> dataSourceIds,
        Expression<Func<T, string?>> queryOwnerUserId)
    {
        return set.Ids == null ? source : source.Where(AllSourcesInOneProjectOf(context, set, dataSourceIds, queryOwnerUserId));
    }

    /// <summary>
    /// Query versions whose recorded data sources one project in the set holds all of (or that are all openly reachable).
    /// A version recorded before Beacon kept its sources (null) is kept here; the approvals list checks it per row.
    /// </summary>
    public static IQueryable<T> WhereVersionSourcesAccessible<T>(
        this IQueryable<T> source,
        BeaconContext context,
        ProjectSet set,
        Expression<Func<T, int[]?>> versionDataSourceIds)
    {
        if (set.Ids == null)
        {
            return source;
        }

        var reachable = AllSourcesInOneProject(context, set);
        var recorded = Expression.Parameter(typeof(int[]), "x");
        var predicate = Expression.Lambda<Func<int[]?, bool>>(
            Expression.OrElse(
                Expression.Equal(recorded, Expression.Constant(null, typeof(int[]))),
                ReplacingExpressionVisitor.Replace(reachable.Parameters[0], recorded, reachable.Body)),
            recorded);

        return source.Where(Compose(versionDataSourceIds, predicate));
    }

    /// <summary>
    /// The predicate behind <see cref="WhereAllSourcesInOneProject{T}"/>, for a count or subquery inside a projection;
    /// <c>x => true</c> when the set reaches every project.
    /// </summary>
    public static Expression<Func<T, bool>> AllSourcesInOneProjectOf<T>(
        BeaconContext context,
        ProjectSet set,
        Expression<Func<T, IEnumerable<int>>> dataSourceIds)
    {
        return set.Ids == null ? x => true : Compose(dataSourceIds, AllSourcesInOneProject(context, set));
    }

    /// <summary>
    /// The predicate behind the query-owner overload of
    /// <see cref="WhereAllSourcesInOneProject{T}(IQueryable{T}, BeaconContext, ProjectSet, Expression{Func{T, IEnumerable{int}}}, Expression{Func{T, string}})"/>,
    /// for a count or subquery inside a projection; <c>x => true</c> when the set reaches every project.
    /// </summary>
    public static Expression<Func<T, bool>> AllSourcesInOneProjectOf<T>(
        BeaconContext context,
        ProjectSet set,
        Expression<Func<T, IEnumerable<int>>> dataSourceIds,
        Expression<Func<T, string?>> queryOwnerUserId)
    {
        if (set.Ids == null)
        {
            return x => true;
        }

        var reachable = Compose(dataSourceIds, AllSourcesInOneProject(context, set));
        if (set.QueryOwnerUserId == null)
        {
            return reachable;
        }

        // A query without steps is reached by its owner: no data source, and the owner is the caller.
        var owner = set.QueryOwnerUserId;
        var hasNoSource = Compose(dataSourceIds, x => !x.Any());
        var isOwner = Compose(queryOwnerUserId, x => x == owner);
        var parameter = dataSourceIds.Parameters[0];
        var body = Expression.OrElse(
            reachable.Body,
            Expression.AndAlso(
                hasNoSource.Body,
                ReplacingExpressionVisitor.Replace(isOwner.Parameters[0], parameter, isOwner.Body)));

        return Expression.Lambda<Func<T, bool>>(body, parameter);
    }

    /// <summary>The predicate on one data source id behind <see cref="WhereSourceAccessible{T}(IQueryable{T}, BeaconContext, ProjectSet, Expression{Func{T, int}})"/>.</summary>
    internal static Expression<Func<int, bool>> SourceAccessible(BeaconContext context, ProjectSet set)
    {
        if (set.Ids == null)
        {
            return x => true;
        }

        var ids = set.Ids.ToList();
        var notOpen = set.NotOpenDataSourceIds.ToList();
        if (set.IncludesOpen && ids.Count > 0)
        {
            return x => context.ProjectDataSources
                    .Where(y => ids.Contains(y.ProjectId))
                    .Any(y => y.DataSourceId == x)
                || !notOpen.Contains(x);
        }

        if (set.IncludesOpen)
        {
            return x => !notOpen.Contains(x);
        }

        if (ids.Count > 0)
        {
            return x => context.ProjectDataSources
                .Where(y => ids.Contains(y.ProjectId))
                .Any(y => y.DataSourceId == x);
        }

        return x => false;
    }

    /// <summary>
    /// The predicate on a set of data source ids behind <see cref="WhereAllSourcesInOneProject{T}(IQueryable{T}, BeaconContext, ProjectSet, Expression{Func{T, IEnumerable{int}}})"/>:
    /// an empty set is never reached through a project or the openly reachable sources.
    /// </summary>
    internal static Expression<Func<IEnumerable<int>, bool>> AllSourcesInOneProject(BeaconContext context, ProjectSet set)
    {
        if (set.Ids == null)
        {
            return x => true;
        }

        var ids = set.Ids.ToList();
        var notOpen = set.NotOpenDataSourceIds.ToList();
        if (set.IncludesOpen && ids.Count > 0)
        {
            return x => x.Any()
                && (ids.Any(y => x.All(z => context.ProjectDataSources
                        .Where(w => w.ProjectId == y)
                        .Any(w => w.DataSourceId == z)))
                    || x.All(y => !notOpen.Contains(y)));
        }

        if (set.IncludesOpen)
        {
            return x => x.Any() && x.All(y => !notOpen.Contains(y));
        }

        if (ids.Count > 0)
        {
            return x => x.Any()
                && ids.Any(y => x.All(z => context.ProjectDataSources
                    .Where(w => w.ProjectId == y)
                    .Any(w => w.DataSourceId == z)));
        }

        return x => false;
    }

    // x => predicate(selector(x)): the selector's body takes the predicate's parameter's place.
    private static Expression<Func<T, bool>> Compose<T, TValue>(
        Expression<Func<T, TValue>> selector,
        Expression<Func<TValue, bool>> predicate)
    {
        var body = ReplacingExpressionVisitor.Replace(predicate.Parameters[0], selector.Body, predicate.Body);

        return Expression.Lambda<Func<T, bool>>(body, selector.Parameters);
    }
}
