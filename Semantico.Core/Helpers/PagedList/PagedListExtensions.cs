using System.ComponentModel.DataAnnotations;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;

namespace Semantico.Core.Helpers;

public static class PagedListExtensions
{
    public static async Task<PagedList<T>> ToPagedListWithoutCountAsync<T>(this IQueryable<T> query, BaseListRequest request, CancellationToken cancellationToken)
    where T : class
    {
        var data = await query
            .ApplySorters(request as SortedListRequest)
            .Skip(request.Page * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedList<T>(data);
    }

    public static async Task<PagedList<T>> ToPagedListAsync<T>(this IQueryable<T> query, BaseListRequest request, CancellationToken cancellationToken)
        where T : class
    {
        var totalCount = await query.CountAsync(cancellationToken);

        var data = await query
            .ApplySorters(request as SortedListRequest)
            .Skip(request.Page * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedList<T>(data, totalCount);
    }

    public static PagedList<T> ToPagedList<T>(this IQueryable<T> query, BaseListRequest request, CancellationToken cancellationToken)
        where T : class
    {
        var totalCount = query.Count();

        var data = query
            .ApplySorters(request as SortedListRequest)
            .Skip(request.Page * request.PageSize)
            .Take(request.PageSize)
            .ToList();

        return new PagedList<T>(data, totalCount);
    }

    public static Task<List<T>> ToSortedListAsync<T>(this IQueryable<T> query, SortedListRequest? sortedListRequest, CancellationToken cancellationToken)
        where T : class
    {
        return query
           .ApplySorters(sortedListRequest)
           .ToListAsync(cancellationToken);
    }

    private static IOrderedQueryable<T> ApplySorters<T>(this IQueryable<T> source, SortedListRequest? sortedListRequest)
        where T : class
    {
        if (sortedListRequest == null)
        {
            return source.OrderByProperty(null, SortDirection.Ascending);
        }

        var criteria = sortedListRequest.SortCriteria;
        foreach (var criterion in criteria)
        {
            source = ApplySorter(source, criterion);
        }

        return (IOrderedQueryable<T>)source;
    }

    private static IOrderedQueryable<T> ApplySorter<T>(IQueryable<T> source, SortCriterion sortCriterion)
        where T : class
    {
        if (string.IsNullOrWhiteSpace(sortCriterion.SortColumn))
        {
            return source.OrderByProperty(null, SortDirection.Ascending);
        }

        return source.OrderByProperty(sortCriterion.SortColumn, sortCriterion.SortDirection);
    }

    /// <summary>
    /// Sort source by provided propertyName, defaults to Id, or first property name found
    /// </summary>
    private static IOrderedQueryable<T> OrderByProperty<T>(this IQueryable<T> source, string? propertyName, SortDirection sortDirection)
        where T : class
    {
        // https://github.com/dotnet/efcore/issues/27330
        var isFirst = !(source.Expression.Type.IsGenericType
                        && source.Expression.Type.GetGenericTypeDefinition()
                            .IsAssignableTo(typeof(IOrderedQueryable<>)));
        if (string.IsNullOrWhiteSpace(propertyName))
        {
            propertyName = GetDefaultPropertyName<T>();
        }

        // construct lambda key selector
        var parameter = Expression.Parameter(typeof(T), "x");
        var orderByProperty = Expression.Property(parameter, propertyName);
        var lambda = Expression.Lambda(orderByProperty, new[] { parameter });

        // instantiate OrderBy method
        var typeArguments = new[] { typeof(T), orderByProperty.Type };

        var genericMethod = GetOrderMethod().MakeGenericMethod(typeArguments);
        var methodResult = genericMethod.Invoke(null, new object[] { source, lambda });

        return (IOrderedQueryable<T>)methodResult!;

        MethodInfo GetOrderMethod()
        {
            var methodName = GetMethodName();

            return typeof(Queryable)
                .GetMethods()
                .Where(method => method.Name == methodName)
                .Where(method => method.GetParameters().Length == 2)
                .Single();
        }

        string GetMethodName()
        {
            if (isFirst)
            {
                return sortDirection == SortDirection.Descending ? nameof(Queryable.OrderByDescending) : nameof(Queryable.OrderBy);
            }
            else
            {
                return sortDirection == SortDirection.Descending ? nameof(Queryable.ThenByDescending) : nameof(Queryable.ThenBy);
            }
        }
    }

    /// <summary>
    /// Get primary key name or first property name if no primary key is defined
    /// </summary>
    private static string GetDefaultPropertyName<T>()
        where T : class
    {
        var properties = typeof(T).GetProperties();

        var key = properties.FirstOrDefault(p => p.IsDefined(typeof(KeyAttribute))) ?? properties.FirstOrDefault();

        return key!.Name;
    }
}