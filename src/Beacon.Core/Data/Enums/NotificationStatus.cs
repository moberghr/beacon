namespace Beacon.Core.Data.Enums;

public enum NotificationStatus
{
    Created = 1,
    NotificationSent = 2,
    NotificationSilenced = 3,
    NoResults = 4,
    Timeout = 5,
    BelowThreshold = 6,
    Failed = 7,

    /// <summary>
    /// A scheduled run that did not run: its owner may no longer run the subscription's query (an inactive owner, a lost
    /// membership, or an ownerless subscription on data that is not openly reachable). Nothing ran and nothing was
    /// delivered; not a failure.
    /// </summary>
    Skipped = 8
}