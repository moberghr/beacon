namespace Beacon.Core.Authorization;

/// <summary>
/// The functional permissions (<see cref="BeaconPermissions"/>) a caller holds now. An Admin holds every one of them.
/// </summary>
public sealed record BeaconPermissionSet(bool IsAdmin, IReadOnlySet<string> Permissions)
{
    /// <summary>No permission: an anonymous caller, an API key, an MCP caller, a user with no functional role.</summary>
    public static BeaconPermissionSet None { get; } = new(false, new HashSet<string>());

    /// <summary>An Admin: every functional permission.</summary>
    public static BeaconPermissionSet Admin { get; } = new(true, BeaconPermissions.All.ToHashSet());

    /// <summary>True for an Admin, or when <paramref name="permission"/> is held.</summary>
    public bool Holds(string permission)
    {
        return IsAdmin || Permissions.Contains(permission);
    }

    /// <summary>True for an Admin, or when any of <paramref name="permissions"/> is held.</summary>
    public bool HoldsAny(IEnumerable<string> permissions)
    {
        return IsAdmin || permissions.Any(Permissions.Contains);
    }
}
