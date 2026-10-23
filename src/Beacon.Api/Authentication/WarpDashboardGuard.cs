namespace Beacon.Api.Authentication;

/// <summary>
/// Fail-closed check for a host that maps the Warp dashboard: Warp's own default (no authorization filter) lets every
/// request that reaches the route in, and Beacon's login gate does not cover the dashboard's path. Call it with the filter
/// before <c>UseWarpUI</c> takes the options; it throws when there is none.
/// </summary>
public static class WarpDashboardGuard
{
    public static T Require<T>(T? authorizationFilter, string routePrefix = "/warp")
        where T : class
    {
        return authorizationFilter ?? throw new InvalidOperationException(
            $"The Warp dashboard at '{routePrefix}' has no authorization filter, so it would be open to every request that reaches it. Set the dashboard options' Authorization to a filter that admits only the callers who may see jobs (the sample host's WarpDashboardAuthFilter admits authenticated Admins).");
    }
}
