namespace Semantico.Core.Data.Enums;

public enum NotificationStatus
{
    Created = 1,
    NotificationSent = 2,
    NotificationSilenced = 3,
    NoResults = 4,
    Timeout = 5,
    BelowThreshold = 6,
    Failed = 7
}