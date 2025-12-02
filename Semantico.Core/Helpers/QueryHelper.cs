using Semantico.Core.Models.Subscriptions;

namespace Semantico.Core.Helpers;

internal class QueryHelper
{
    public static string CompileSql(string querySql, List<SubscriptionParameterData>? parameterValues)
    {
        if (parameterValues == null || parameterValues.Count == 0)
        {
            return querySql;
        }

        foreach (var parameter in parameterValues)
        {
            if (string.IsNullOrEmpty(parameter.QueryPlaceholder))
            {
                continue;
            }

            var escapedValue = EscapeSqlValue(parameter.Value);
            querySql = querySql.Replace(parameter.QueryPlaceholder, escapedValue);
        }

        return querySql;
    }

    private static string EscapeSqlValue(string? value)
    {
        if (value == null)
        {
            return "NULL";
        }

        // Escape single quotes by doubling them (standard SQL escaping)
        return value.Replace("'", "''");
    }
}
