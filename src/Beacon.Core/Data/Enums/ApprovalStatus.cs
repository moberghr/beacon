namespace Beacon.Core.Data.Enums;

public enum ApprovalStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,

    /// <summary>A newer submission for the same query replaced this request before anyone decided it.</summary>
    Superseded = 4
}
