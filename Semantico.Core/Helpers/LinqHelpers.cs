using System.Linq.Expressions;

namespace Semantico.Core.Helpers;

public static class LinqHelpers
{
    public static IQueryable<T> WhereIf<T>(this IQueryable<T> source, bool condition, Expression<Func<T, bool>> selector)
    {
        if (!condition)
        {
            return source;
        }

        return source.Where(selector);
    }

    public static IQueryable<T> TakeIf<T>(this IQueryable<T> source, bool condition, int? itemCount)
    {
        if (condition == false || itemCount.HasValue == false)
        {
            return source;
        }

        return source.Take(itemCount.Value);
    }
}

