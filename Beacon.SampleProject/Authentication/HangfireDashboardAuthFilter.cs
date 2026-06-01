using Hangfire.Dashboard;
using Beacon.Core.Services;

namespace Beacon.SampleProject.Authentication;

/// <summary>
/// Restricts the Hangfire dashboard to authenticated users in the Admin role.
/// Without this filter, Hangfire's default behaviour outside the local-only filter
/// allows any authenticated user to view and trigger jobs — see §7.4 / §1.x.
/// </summary>
internal sealed class HangfireDashboardAuthFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var user = context.GetHttpContext().User;
        return user.Identity?.IsAuthenticated == true
            && user.IsInRole(RoleService.RoleNames.Admin);
    }
}
