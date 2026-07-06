namespace Beacon.Core.Data.Enums;

/// <summary>
/// Types of actions an AI Actor can perform during a think cycle
/// </summary>
public enum AiActorActionType
{
    /// <summary>
    /// Create a new query to monitor the data source
    /// </summary>
    CreateQuery = 1,

    /// <summary>
    /// Create a subscription for an existing query
    /// </summary>
    CreateSubscription = 2,

    /// <summary>
    /// Modify an existing query (update SQL, parameters, etc.)
    /// </summary>
    RefineQuery = 3,

    /// <summary>
    /// Archive an underperforming or no longer needed query
    /// </summary>
    ArchiveQuery = 4,

    /// <summary>
    /// Archive a subscription that is no longer needed
    /// </summary>
    ArchiveSubscription = 5,

    /// <summary>
    /// Send a notification about important findings
    /// </summary>
    SendNotification = 6
}
