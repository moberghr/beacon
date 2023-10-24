using Semantico.Api.Data.Entities;
using Semantico.Api.Handlers.Subscriptions;
using Semantico.Api.Validators;

namespace Semantico.Api.Helpers;

public class QueryHelper
{
    public static string CompileSql(string querySql, List<SubscriptionParameterResponseListData> parameterValues)
    {
        foreach (var parameter in parameterValues)
        {
            querySql = querySql.Replace(parameter.QueryPlaceholder, parameter.Value);
        }

        return querySql;
    }
}
