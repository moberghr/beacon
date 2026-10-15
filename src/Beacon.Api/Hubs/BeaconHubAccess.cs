using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Beacon.Core;
using Beacon.Core.Authorization;

namespace Beacon.Api.Hubs;

/// <summary>
/// Who may open a <see cref="BeaconHub"/> connection (and whom <c>/auth/me</c> tells that realtime is on): with
/// authorization or Beacon user management on, a caller with read permission (Viewer or above, or an Admin), or a holder
/// of a functional permission whose pages refresh from hub events: Approver (approval updates) and MigrationOperator (job
/// status of the migrations they run). Holders of other functional permissions only (Auditor, SecurityAdmin, UserAdmin,
/// DataSourceAdmin) get no connection. Decided by the authorization provider and the permission accessor, like the REST
/// routes. A host with neither authorization nor user management on admits every signed-in caller; without Beacon's
/// configuration (Beacon not added to the host) nobody is admitted.
/// </summary>
internal static class BeaconHubAccess
{
    // The functional permissions whose pages listen to hub events.
    private static readonly string[] HubPermissions = [BeaconPermissions.Approver, BeaconPermissions.MigrationOperator];

    public static async Task<bool> AllowsAsync(HttpContext httpContext)
    {
        var services = httpContext.RequestServices;
        var configuration = services?.GetService<BeaconConfiguration>();
        if (services == null || configuration == null)
        {
            return false;
        }

        if (!configuration.Authorization.Enabled && !configuration.UserManagement.Enabled)
        {
            return true;
        }

        var authorization = services.GetRequiredService<IBeaconAuthorizationProvider>();
        if (await authorization.HasReadPermissionAsync(httpContext.RequestAborted))
        {
            return true;
        }

        var accessor = services.GetService<IBeaconPermissionAccessor>();
        if (accessor == null)
        {
            return false;
        }

        var permissions = await accessor.GetCurrentAsync(httpContext.RequestAborted);
        foreach (var permission in HubPermissions.Where(permissions.Holds))
        {
            if (await authorization.AllowsFunctionalPermissionAsync(permission, httpContext.RequestAborted))
            {
                return true;
            }
        }

        return false;
    }
}
