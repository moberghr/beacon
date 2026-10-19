using System.Linq.Expressions;

namespace Beacon.Core.Helpers;

internal static class LinqHelpers
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
