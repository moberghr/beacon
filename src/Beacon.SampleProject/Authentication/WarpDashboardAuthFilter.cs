using Warp.UI;
using Beacon.Core.Services;

namespace Beacon.SampleProject.Authentication;

/// <summary>
/// Restricts the Warp dashboard to authenticated users in the Admin role. Without this filter
/// Warp's dashboard would be reachable by any request that gets past the app's auth pipeline —
/// see §7.4 / §1.x.
/// </summary>
internal sealed class WarpDashboardAuthFilter : IWarpAuthorizationFilter
{
    public bool Authorize(HttpContext httpContext)
    {
        var user = httpContext.User;

        return user.Identity?.IsAuthenticated == true
            && user.IsInRole(RoleService.RoleNames.Admin);
    }
}
