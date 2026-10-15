using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;

namespace Beacon.Api.Authentication;

/// <summary>
/// The requests Beacon's authentication middlewares handle: <c>/beacon</c> and the prefixes in
/// <c>Beacon:Authentication:AdditionalPaths</c> (<see cref="AuthenticationPathOptions"/>), matched by whole segments and
/// ignoring case against <see cref="HttpRequest.Path"/>, which excludes the host's path base. The API-key, bearer JWT and
/// cookie middlewares and the login and first-run redirects pass every other request on untouched, so a Beacon credential
/// never authenticates a host route.
/// </summary>
public static class BeaconRequestPaths
{
    public static bool IsBeaconRequest(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments(AuthenticationPathOptions.BeaconPath, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var options = context.RequestServices?.GetService<IOptions<AuthenticationPathOptions>>()?.Value;
        if (options == null)
        {
            return false;
        }

        foreach (var entry in options.AdditionalPaths)
        {
            // Validated at startup; an entry that is not a path prefix is never matched, so it cannot widen the scope.
            var prefix = AuthenticationPathOptions.Normalize(entry);
            if (AuthenticationPathOptions.IsValidPrefix(prefix)
                && path.StartsWithSegments(new PathString(prefix), StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
