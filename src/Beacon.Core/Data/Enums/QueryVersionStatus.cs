namespace Beacon.Core.Data.Enums;

public enum QueryVersionStatus
{
    Active = 1,
    Archived = 2,
    PendingApproval = 3,
    Rejected = 4,

    /// <summary>A proposal whose approval request a newer submission superseded: never applied.</summary>
    Superseded = 5
}
