using Beacon.Core.Helpers;
using Beacon.Core.Models;
using Beacon.Core.Models.Queries;
using Beacon.Core.Models.Subscriptions;

namespace Beacon.Core.Validators;

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
            throw new BeaconException($"Defined subscription parameters count does not match specified query parameter count.");
        }

        // Each query placeholder must be matched EXACTLY once. A single running counter (the old
        // approach) let a duplicated placeholder and a missing one cancel out — e.g. query [@a,@b]
        // with subscription [@a,@a] passed while @b was never bound. Count matches per query param.
        foreach (var queryParam in queryParameters)
        {
            var matches = subscriptionParameters
                .Where(x => x.QueryPlaceholder == queryParam.Placeholder)
                .ToList();

            if (matches.Count == 0)
            {
                throw new BeaconException($"Not all requested query parameters are defined.");
            }

            if (matches.Count > 1)
            {
                throw new BeaconException($"There are multiple of the same query parameter names defined");
            }

            var subscriptionParam = matches[0];
            QueryValidator.CheckForFlaggedWords(subscriptionParam.Value);
            ParameterTypeHelper.ParseParameter(subscriptionParam.Value, queryParam.Type);
        }
    }
}