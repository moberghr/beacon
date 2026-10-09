using Beacon.AI.Services.Ai.AiActor.Models;
using Beacon.Core.Data.Enums;

namespace Beacon.AI.Services.Ai.AiActor;

/// <summary>
/// Pure decisions for AI actor actions: whether an action may execute at all, how a plan is
/// recorded when it may not, and the ownership / lock / cap checks the executors enforce.
/// Every check returns an error message, or null when the action is allowed.
/// </summary>
internal static class AiActorActionGuard
{
    public static bool ShouldExecute(Beacon.Core.Data.Entities.AiActor actor)
    {
        return !actor.RequiresApproval;
    }

    public static AiActorAction ToProposed(AiActorActionPlan plan)
    {
        var action = new AiActorAction
        {
            Reasoning = plan.Reasoning,
            Proposed = true,
            Success = false
        };

        // The plan is deserialized LLM output: "actionType" or "parameters" can arrive as JSON null.
        if (string.IsNullOrWhiteSpace(plan.ActionType) || plan.Parameters == null)
        {
            action.Proposed = false;
            action.ErrorMessage = "Unknown action type";
            return action;
        }

        switch (plan.ActionType.ToUpperInvariant())
        {
            case "CREATE_QUERY":
                action.ActionType = AiActorActionType.CreateQuery;
                action.QueryName = Text(plan, "name");
                action.SqlQuery = Text(plan, "sql");
                break;

            case "CREATE_SUBSCRIPTION":
                action.ActionType = AiActorActionType.CreateSubscription;
                action.SubscriptionQueryId = Number(plan, "queryId");
                action.QueryName = Text(plan, "queryName");
                action.CronExpression = Text(plan, "cronExpression");
                break;

            case "REFINE_QUERY":
                action.ActionType = AiActorActionType.RefineQuery;
                action.TargetQueryId = Number(plan, "queryId");
                action.SqlQuery = Text(plan, "newSql");
                break;

            case "ARCHIVE_QUERY":
                action.ActionType = AiActorActionType.ArchiveQuery;
                action.TargetQueryId = Number(plan, "queryId");
                break;

            case "ARCHIVE_SUBSCRIPTION":
                action.ActionType = AiActorActionType.ArchiveSubscription;
                action.TargetSubscriptionId = Number(plan, "subscriptionId");
                break;

            default:
                action.Proposed = false;
                action.ErrorMessage = $"Unknown action type: {plan.ActionType}";
                break;
        }

        return action;
    }

    public static string? CheckOwnership(int actorId, int? entityActorId, bool isLocked)
    {
        if (entityActorId != actorId)
        {
            return "Not owned by this actor";
        }

        if (isLocked)
        {
            return "Locked and cannot be modified by AI";
        }

        return null;
    }

    public static string? CheckQueryCap(int ownedQueries, int maxQueries)
    {
        if (ownedQueries >= maxQueries)
        {
            return $"Actor query limit reached ({maxQueries})";
        }

        return null;
    }

    public static string? CheckSubscriptionCap(int existingForQuery, int maxPerQuery)
    {
        if (existingForQuery >= maxPerQuery)
        {
            return $"Actor subscription limit per query reached ({maxPerQuery})";
        }

        return null;
    }

    private static string? Text(AiActorActionPlan plan, string key)
    {
        return plan.Parameters.GetValueOrDefault(key)?.ToString();
    }

    private static int? Number(AiActorActionPlan plan, string key)
    {
        return int.TryParse(Text(plan, key), out var value) ? value : null;
    }
}
