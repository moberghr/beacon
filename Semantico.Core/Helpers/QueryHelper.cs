using Semantico.Core.Models.Subscriptions;

namespace Semantico.Core.Helpers;

public class QueryHelper
{
    public static string CompileSql(string querySql, List<SubscriptionParamaterData> parameterValues)
    {
        foreach (var parameter in parameterValues)
        {
            querySql = querySql.Replace(parameter.QueryPlaceholder, parameter.Value);
        }

        return querySql;
    }
}
