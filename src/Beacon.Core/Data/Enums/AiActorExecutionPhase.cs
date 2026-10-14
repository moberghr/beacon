namespace Beacon.Core.Data.Enums;

/// <summary>
/// Phases of an AI Actor think cycle execution
/// </summary>
public enum AiActorExecutionPhase
{
    /// <summary>
    /// Analyzing current queries, subscriptions, and recent results
    /// </summary>
    Analyzing = 1,

    /// <summary>
    /// Planning actions based on analysis
    /// </summary>
    Planning = 2,

    /// <summary>
    /// Executing planned actions (creating/modifying queries, subscriptions)
    /// </summary>
    Executing = 3,

    /// <summary>
    /// Sending notifications for important findings
    /// </summary>
    Notifying = 4,

    /// <summary>
    /// Think cycle completed successfully
    /// </summary>
    Completed = 5,

    /// <summary>
    /// Think cycle failed with an error
    /// </summary>
    Failed = 6,

    /// <summary>
    /// A background think cycle that did not run: the actor's creator may no longer work with its data source (an inactive
    /// creator, a lost membership, or an actor without a creator on data that is not openly reachable). No LLM call and no
    /// SQL ran; not a failure.
    /// </summary>
    Skipped = 7
}
