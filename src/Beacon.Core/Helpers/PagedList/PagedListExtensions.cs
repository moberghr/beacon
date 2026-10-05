using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Beacon.Core.Helpers;

/// <summary>
/// Paging and sorting for list handlers. Call it on the projected query (after <c>.Select(new …)</c>), so
/// sort columns resolve against the list item the client sees, and use a member initialiser in that
/// projection — EF cannot see through a positional <c>new Dto(a, b).Name</c> to build an ORDER BY.
/// </summary>
public static class PagedListExtensions
{
    private const string DefaultTiebreaker = "Id";

    /// <summary>
    /// Counts the filtered set, then returns the requested page sorted by the request's criteria, else
    /// <paramref name="defaultSort"/> (same <c>-column,column</c> syntax), always ending on the unique
    /// <paramref name="tiebreaker"/> column when the item has it, so pages never overlap or skip rows.
    /// </summary>
    public static async Task<PagedList<T>> ToPagedListAsync<T>(
        this IQueryable<T> query,
        ListRequest request,
        CancellationToken cancellationToken,
        string? defaultSort = null,
        string tiebreaker = DefaultTiebreaker)
    {
        var totalCount = await query.CountAsync(cancellationToken);
        var items = totalCount == 0
            ? []
            : await query
                .ApplyListRequest(request, defaultSort, tiebreaker)
                .ToListAsync(cancellationToken);

        return PagedList<T>.Create(items, totalCount, request.PageSizeOrDefault);
    }

    /// <summary>
    /// For a query the handler has already ordered itself (computed sort keys the generic column sort cannot
    /// express). The handler owns determinism: end the ordering on a unique column.
    /// </summary>
    public static async Task<PagedList<T>> ToPagedListPreservingOrderAsync<T>(
        this IQueryable<T> query,
        ListRequest request,
        CancellationToken cancellationToken)
    {
        var totalCount = await query.CountAsync(cancellationToken);
        var items = totalCount == 0
            ? []
            : await query
                .Skip(request.PageOrDefault * request.PageSizeOrDefault)
                .Take(request.PageSizeOrDefault)
                .ToListAsync(cancellationToken);

        return PagedList<T>.Create(items, totalCount, request.PageSizeOrDefault);
    }

    /// <summary>
    /// For lists assembled in memory (merged or grouped client-side). Same contract as the EF overload.
    /// </summary>
    public static PagedList<T> ToPagedList<T>(
        this IEnumerable<T> source,
        ListRequest request,
        string? defaultSort = null,
        string tiebreaker = DefaultTiebreaker)
    {
        var all = source as IReadOnlyCollection<T> ?? source.ToList();
        var items = all
            .AsQueryable()
            .ApplyListRequest(request, defaultSort, tiebreaker)
            .ToList();

        return PagedList<T>.Create(items, all.Count, request.PageSizeOrDefault);
    }

    /// <summary>Sort + skip + take, without executing — what translation tests call <c>ToQueryString()</c> on.</summary>
    public static IQueryable<T> ApplyListRequest<T>(
        this IQueryable<T> query,
        ListRequest request,
        string? defaultSort = null,
        string tiebreaker = DefaultTiebreaker) =>
        query
            .ApplySort(request.SortCriteria(), defaultSort, tiebreaker)
            .Skip(request.PageOrDefault * request.PageSizeOrDefault)
            .Take(request.PageSizeOrDefault);

    public static IOrderedQueryable<T> ApplySort<T>(
        this IQueryable<T> query,
        IReadOnlyList<SortCriterion> criteria,
        string? defaultSort = null,
        string tiebreaker = DefaultTiebreaker)
    {
        // Unknown columns are dropped rather than thrown on: a stale sort in a client's URL must not turn
        // every request into a 500. Only when nothing usable is left does the handler's default apply.
        var paths = ResolveSortable<T>(criteria);
        if (paths.Count == 0)
        {
            paths = ResolveSortable<T>(ListRequest.ParseSort(defaultSort));
        }

        var tiebreakerPath = ResolvePath(typeof(T), tiebreaker);
        if (tiebreakerPath != null && !paths.Any(x => x.Path.SequenceEqual(tiebreakerPath)))
        {
            paths.Add((tiebreakerPath, SortDirection.Ascending));
        }

        IOrderedQueryable<T>? ordered = null;
        foreach (var (path, direction) in paths)
        {
            ordered = OrderByPath(ordered ?? query, path, direction, isFirst: ordered == null);
        }

        // No sortable column and no tiebreaker: keep the source order, typed so paging still composes.
        return ordered ?? query.OrderBy(x => 0);
    }

    private static List<(List<PropertyInfo> Path, SortDirection Direction)> ResolveSortable<T>(IReadOnlyList<SortCriterion> criteria)
    {
        var resolved = new List<(List<PropertyInfo> Path, SortDirection Direction)>();
        foreach (var criterion in criteria)
        {
            var path = ResolvePath(typeof(T), criterion.SortColumn);
            if (path != null && !resolved.Any(x => x.Path.SequenceEqual(path)))
            {
                resolved.Add((path, criterion.SortDirection));
            }
        }

        return resolved;
    }

    // Case-insensitive, because clients send the camelCase names of their JSON contract. Supports
    // navigation paths ("dataSource.name"), and only lands on scalar types — ordering by a collection or a
    // nested object has no SQL translation.
    private static List<PropertyInfo>? ResolvePath(Type type, string? column)
    {
        if (string.IsNullOrWhiteSpace(column))
        {
            return null;
        }

        var path = new List<PropertyInfo>();
        var current = type;
        foreach (var segment in column.Split('.'))
        {
            var property = current.GetProperty(segment, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property == null)
            {
                return null;
            }

            path.Add(property);
            current = property.PropertyType;
        }

        return IsSortableType(current) ? path : null;
    }

    private static bool IsSortableType(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;

        return underlying.IsPrimitive
            || underlying.IsEnum
            || underlying == typeof(string)
            || underlying == typeof(decimal)
            || underlying == typeof(DateTime)
            || underlying == typeof(DateTimeOffset)
            || underlying == typeof(DateOnly)
            || underlying == typeof(TimeSpan)
            || underlying == typeof(Guid);
    }

    private static IOrderedQueryable<T> OrderByPath<T>(IQueryable<T> source, List<PropertyInfo> path, SortDirection direction, bool isFirst)
    {
        var parameter = Expression.Parameter(typeof(T), "x");
        Expression body = parameter;
        foreach (var property in path)
        {
            body = Expression.Property(body, property);
        }

        var methodName = (isFirst, direction) switch
        {
            (true, SortDirection.Descending) => nameof(Queryable.OrderByDescending),
            (true, _) => nameof(Queryable.OrderBy),
            (false, SortDirection.Descending) => nameof(Queryable.ThenByDescending),
            (false, _) => nameof(Queryable.ThenBy),
        };

        var method = typeof(Queryable)
            .GetMethods()
            .Where(x => x.Name == methodName)
            .Where(x => x.GetParameters().Length == 2)
            .First()
            .MakeGenericMethod(typeof(T), body.Type);

        return (IOrderedQueryable<T>)method.Invoke(null, [source, Expression.Lambda(body, parameter)])!;
    }
}
