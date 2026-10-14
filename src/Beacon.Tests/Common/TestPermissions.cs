using System.Security.Claims;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Moq;

namespace Beacon.Tests.Common;

/// <summary>A permission accessor answering a fixed set of functional permissions, for handler and authorizer tests.</summary>
internal static class TestPermissions
{
    /// <summary>No functional permission.</summary>
    public static IBeaconPermissionAccessor None => Of(BeaconPermissionSet.None);

    /// <summary>An Admin: every functional permission.</summary>
    public static IBeaconPermissionAccessor Admin => Of(BeaconPermissionSet.Admin);

    /// <summary>A non-Admin holding <paramref name="permissions"/>.</summary>
    public static IBeaconPermissionAccessor Holding(params string[] permissions)
    {
        return Of(new BeaconPermissionSet(false, permissions.ToHashSet()));
    }

    /// <summary>
    /// What a session's role claims grant with the default role names: everything for the Admin role, else the permissions
    /// whose role it carries.
    /// </summary>
    public static IBeaconPermissionAccessor ForPrincipal(ClaimsPrincipal? user)
    {
        if (BeaconActor.HoldsAdminRole(user))
        {
            return Admin;
        }

        var roles = user?.FindAll(ClaimTypes.Role).Select(x => x.Value) ?? [];

        return Of(new BeaconPermissionSet(false, new RoleMappingOptions().PermissionsOfRoles(roles)));
    }

    public static IBeaconPermissionAccessor Of(BeaconPermissionSet set)
    {
        var accessor = new Mock<IBeaconPermissionAccessor>();
        accessor
            .Setup(x => x.GetCurrentAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(set);

        return accessor.Object;
    }
}
