namespace Semantico.Core.Data.Enums;

/// <summary>
/// Status of an AI Actor's proposed plan.
/// </summary>
public enum AiActorPlanStatus
{
    /// <summary>
    /// Plan is awaiting user review and approval
    /// </summary>
    PendingApproval = 1,

    /// <summary>
    /// Plan has been approved and is currently being executed
    /// </summary>
    Executing = 2,

    /// <summary>
    /// Plan was approved and has been fully executed
    /// </summary>
    Executed = 3,

    /// <summary>
    /// Plan was rejected by the user
    /// </summary>
    Rejected = 4,

    /// <summary>
    /// Plan expired before being reviewed (e.g., context changed)
    /// </summary>
    Expired = 5,

    /// <summary>
    /// User requested changes to the plan - new revision will be generated
    /// </summary>
    RevisionRequested = 6
}
