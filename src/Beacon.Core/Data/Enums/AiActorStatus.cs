namespace Beacon.Core.Data.Enums;

/// <summary>
/// Status of an AI Actor
/// </summary>
public enum AiActorStatus
{
    /// <summary>
    /// Actor is being configured but not yet active
    /// </summary>
    Draft = 1,

    /// <summary>
    /// Actor is active and will think after subscription executions
    /// </summary>
    Active = 2,

    /// <summary>
    /// Actor is temporarily paused and will not think
    /// </summary>
    Paused = 3,

    /// <summary>
    /// Actor encountered an error and requires attention
    /// </summary>
    Failed = 4,

    /// <summary>
    /// Actor has been archived and is no longer active
    /// </summary>
    Archived = 5
}
