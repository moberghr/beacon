using MediatR;
using Semantico.Api.Data.Entities;

namespace Semantico.Api.Validators;

public static class QueryValidator
{
    private static string[] _flaggedWords = new string[]
    {
        "insert",
        "update",
        "delete",
        "drop",
        "replace",
        "alter"
    };

    public static void CheckForFlaggedWords(string sqlQuery)
    {
        foreach (var flaggedWord in _flaggedWords)
        {
            if (sqlQuery.Contains(flaggedWord, StringComparison.InvariantCultureIgnoreCase))
            {
                throw new Exception("Query contains keywords that are flagged as not allowed.");
            }
        }
    }

    public static void ValidateQueryUpdate(Query query, List<QueryParameter> updateParameters)
    {
        // If parameters aren't impacted, query update is "valid"
        if (updateParameters.Count == 0 && query.Parameters.Count == 0)
        {
            return;
        }

        if (query.Subscriptions.Any())
        {
            try
            {
                foreach (var subscription in query.Subscriptions)
                {
                    SubscriptionValidator.ValidateParameters(subscription.Parameters, updateParameters);
                }
            }
            catch(Exception)
            {
                throw new Exception($"Unable to modify query parameters.");
            }
        }
    }
}
