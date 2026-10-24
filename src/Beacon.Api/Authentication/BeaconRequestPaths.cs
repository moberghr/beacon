using Beacon.Core.Configuration;

namespace Beacon.Api.Authentication;

/// <summary>
/// The requests Beacon's authentication middlewares handle: <c>/beacon</c> and the prefixes in
/// <c>Beacon:Authentication:AdditionalPaths</c> (<see cref="AuthenticationPathOptions"/>), matched by whole segments and
/// ignoring case against <see cref="HttpRequest.Path"/>, which excludes the host's path base. The API-key, bearer JWT and
/// cookie middlewares and the login and first-run redirects pass every other request on untouched, and the cookie
/// scheme's own events (<see cref="BeaconSessionEvents"/>) refuse the <c>Beacon.Auth</c> session there, also when the
/// host's <c>UseAuthentication</c> or policies authenticate with the cookie scheme: a Beacon credential never
/// authenticates a host route. The anonymous embed routes are not Beacon requests while embedding is on
/// (<see cref="BeaconEmbedPaths"/>): no Beacon credential but an embed one authenticates them.
/// </summary>
public static class BeaconRequestPaths
{
    public static bool IsBeaconRequest(HttpContext context)
    {
        // The anonymous embed routes, while embedding is on, are the embed middleware's alone (BeaconEmbedPaths).
        if (BeaconEmbedPaths.IsEmbedRequest(context))
        {
            return false;
        }

        var path = context.Request.Path;
        if (path.StartsWithSegments(AuthenticationPathOptions.BeaconPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The listed prefixes, normalized once (AuthenticationPathPrefixes); without them only /beacon is Beacon's.
        var prefixes = context.RequestServices?.GetService<AuthenticationPathPrefixes>();

        return prefixes != null && Matches(path, prefixes);
    }

    /// <summary>Whether <paramref name="path"/> lies under one of <paramref name="prefixes"/>: whole segments, ignoring case.</summary>
    public static bool Matches(PathString path, AuthenticationPathPrefixes prefixes)
    {
        foreach (var prefix in prefixes.Prefixes)
        {
            if (path.StartsWithSegments(new PathString(prefix), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
