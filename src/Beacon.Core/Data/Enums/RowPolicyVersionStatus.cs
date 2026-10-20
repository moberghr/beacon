namespace Beacon.Core.Data.Enums;

public enum RowPolicyVersionStatus
{
    /// <summary>The approved version the policy enforces.</summary>
    Active = 1,

    /// <summary>A version that was active until a newer one was approved, or until the policy was archived.</summary>
    Archived = 2,

    /// <summary>A submitted version waiting for a second person's approval: not enforced.</summary>
    PendingApproval = 3,

    Rejected = 4,

    /// <summary>A submitted version a newer submission replaced before anyone decided it: never enforced.</summary>
    Superseded = 5
}
