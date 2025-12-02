using Semantico.Core.Data.Entities;
using Semantico.Core.Models.Queries;
using Semantico.Core.Models.Subscriptions;

namespace Semantico.Core.Helpers;

/// <summary>
/// Factory for creating parameter entities from DTOs.
/// Consolidates repeated parameter creation patterns across services.
/// </summary>
internal static class ParameterEntityFactory
{
    /// <summary>
    /// Creates a QueryStepParameter entity from a QueryStepParameterData DTO.
    /// </summary>
    public static QueryStepParameter CreateQueryStepParameter(QueryStepParameterData data, int queryStepId)
    {
        return new QueryStepParameter
        {
            QueryStepId = queryStepId,
            Name = data.Name,
            Type = data.Type,
            Description = data.Description,
            Placeholder = data.Placeholder
        };
    }

    /// <summary>
    /// Creates a QueryStepParameter entity from a QueryParameterData DTO (legacy format).
    /// </summary>
    public static QueryStepParameter CreateQueryStepParameter(QueryParameterData data, int queryStepId)
    {
        return new QueryStepParameter
        {
            QueryStepId = queryStepId,
            Name = data.Name,
            Type = data.Type,
            Description = data.Description,
            Placeholder = data.Placeholder
        };
    }

    /// <summary>
    /// Creates multiple QueryStepParameter entities from a collection of QueryStepParameterData DTOs.
    /// </summary>
    public static List<QueryStepParameter> CreateQueryStepParameters(
        IEnumerable<QueryStepParameterData> parameters,
        int queryStepId)
    {
        return parameters
            .Select(p => CreateQueryStepParameter(p, queryStepId))
            .ToList();
    }

    /// <summary>
    /// Creates multiple QueryStepParameter entities from a collection of QueryParameterData DTOs (legacy format).
    /// </summary>
    public static List<QueryStepParameter> CreateQueryStepParameters(
        IEnumerable<QueryParameterData> parameters,
        int queryStepId)
    {
        return parameters
            .Select(p => CreateQueryStepParameter(p, queryStepId))
            .ToList();
    }

    /// <summary>
    /// Creates a SubscriptionParameter entity from a DTO.
    /// </summary>
    public static SubscriptionParameter CreateSubscriptionParameter(
        SubscriptionParameterData data,
        int? subscriptionId = null)
    {
        var param = new SubscriptionParameter
        {
            QueryPlaceholder = data.QueryPlaceholder ?? string.Empty,
            Value = data.Value ?? string.Empty
        };

        if (subscriptionId.HasValue)
        {
            param.SubscriptionId = subscriptionId.Value;
        }

        return param;
    }

    /// <summary>
    /// Creates multiple SubscriptionParameter entities from a collection of DTOs.
    /// </summary>
    public static List<SubscriptionParameter> CreateSubscriptionParameters(
        IEnumerable<SubscriptionParameterData> parameters,
        int? subscriptionId = null)
    {
        return parameters
            .Select(p => CreateSubscriptionParameter(p, subscriptionId))
            .ToList();
    }
}
