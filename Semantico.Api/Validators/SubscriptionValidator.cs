using MediatR;
using Semantico.Api.Data.Entities;
using Semantico.Api.Helpers;
using Semantico.Api.Types;
using static Microsoft.EntityFrameworkCore.DbLoggerCategory;

namespace Semantico.Api.Validators;

public static class SubscriptionValidator
{
    public static void ValidateParameters(List<SubscriptionParameter> subscriptionParameters, List<QueryParameter> queryParameters)
    {
        // Query does not have user-definable parameters so we will reset/ignore them if they exist.
        if (queryParameters.Count == 0)
        {
            subscriptionParameters.Clear();
            return;
        }

        if (subscriptionParameters.Count != queryParameters.Count)
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

                    try
                    {
                        QueryValidator.CheckForFlaggedWords(subscriptionParam.Value);
                    }
                    catch (SemanticoException)
                    {
                        throw new SemanticoException($"Parameter value contains keywords that are flagged as not allowed.");
                    }

                    if (ParameterTypeHelper.CanParseParameter(subscriptionParam.Value, queryParam.Type) == false)
                    {
                        throw new SemanticoException($"Unable to parse {subscriptionParam.QueryPlaceholder} value to set query parameter type.");
                    }
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
