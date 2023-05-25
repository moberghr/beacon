using System.Linq.Expressions;

namespace Semantico.Api.Helpers;

public static class QueryHelpers
{
    public static IQueryable<T> WhereIf<T>(this IQueryable<T> source, bool condition, Expression<Func<T, bool>> selector)
    {
        if (!condition)
        {
            return source;
        }

        return source.Where(selector);
    }
}

