namespace Beacon.Core.Authorization;

/// <summary>Who manages project memberships in this deployment, as a project's detail reports it.</summary>
public enum ProjectMembershipManagement
{
    /// <summary>Beacon: members are added, changed and removed in Beacon (Beacon user management is on).</summary>
    Beacon = 1,

    /// <summary>The host application, through its own membership source: members are not managed in Beacon.</summary>
    Host = 2,

    /// <summary>No membership source is available: a restricted project is reachable by Admins only, and restricting one is refused.</summary>
    Unavailable = 3
}
