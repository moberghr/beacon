using System.Linq.Expressions;
using Semantico.Core.Data.Entities;
using Semantico.Core.Models.Queries;
using Semantico.Core.Models.Recipients;

namespace Semantico.Core.Helpers;

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

    public static IQueryable<T> TakeIf<T>(this IQueryable<T> source, bool condition, int? itemCount)
    {
        if (condition == false || itemCount.HasValue == false)
        {
            return source;
        }

        return source.Take(itemCount.Value);
    }

    public static IQueryable<T> SkipIf<T>(this IQueryable<T> source, bool condition, int? count)
    {
        if (!condition || !count.HasValue)
        {
            return source;
        }

        return source.Skip(count.Value);
    }

    public static List<QueryStepParameterData> ToQueryStepParameterDataList(this IEnumerable<QueryStepParameter> parameters)
    {
        return parameters.Select(p => new QueryStepParameterData
        {
            Name = p.Name,
            Type = p.Type,
            Description = p.Description,
            Placeholder = p.Placeholder
        }).ToList();
    }

    public static List<RecipientData> ToRecipientDataList(this IEnumerable<Recipient> recipients)
    {
        return recipients.Select(r => new RecipientData
        {
            RecipientId = r.Id,
            Name = r.Name,
            Description = r.Description,
            Destination = r.Destination,
            NotificationType = r.NotificationType
        }).ToList();
    }
}

