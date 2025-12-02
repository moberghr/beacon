using Semantico.Core.Helpers;
using Semantico.Core.Models;
using Semantico.Core.Models.Queries;
using Semantico.Core.Models.Subscriptions;

namespace Semantico.Core.Validators;

internal static class SubscriptionValidator
{
    public static void ValidateParameters(List<SubscriptionParameterData>? subscriptionParameters, List<QueryParameterData> queryParameters)
    {
        // Query does not have user-definable parameters so we will reset/ignore them if they exist.
        if (queryParameters.Count == 0)
        {
            subscriptionParameters?.Clear();
            return;
        }

        if (subscriptionParameters?.Count != queryParameters.Count)
        {
            throw new SemanticoException($"Defined subscription parameters count does not match specified query parameter count.");
        }

        int matched = 0;
        foreach (var queryParam in queryParameters)
        {
            foreach (var subscriptionParam in subscriptionParameters)
            {
                if (subscriptionParam.QueryPlaceholder == queryParam.Placeholder)
                {
                    ++matched;

                    QueryValidator.CheckForFlaggedWords(subscriptionParam.Value);

                    ParameterTypeHelper.ParseParameter(subscriptionParam.Value, queryParam.Type);
                }
            }
        }

        if (matched < queryParameters.Count)
        {
            throw new SemanticoException($"Not all requested query parameters are defined.");
        }

        if (matched > queryParameters.Count)
        {
            throw new SemanticoException($"There are multiple of the same query parameter names defined");
        }
    }
}