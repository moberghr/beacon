using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Beacon.Api.Authentication;

/// <summary>
/// Warns once at startup, outside Development, when Beacon's cookie scheme is the host's default authentication scheme
/// (set as the default, or the only scheme registered). The host's <c>UseAuthentication</c> then signs the
/// <c>Beacon.Auth</c> session in on every route, the host's own included, whatever path the Beacon middlewares are
/// scoped to: a host with endpoints of its own sets its own default scheme, or names its schemes on its policies.
/// </summary>
internal sealed class BeaconCookieDefaultSchemeWarning(
    IAuthenticationSchemeProvider schemes,
    IHostEnvironment environment,
    ILogger<BeaconCookieDefaultSchemeWarning> logger) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        if (environment.IsDevelopment())
        {
            return next;
        }

        // The framework's provider answers from its options, without I/O.
        var scheme = schemes.GetDefaultAuthenticateSchemeAsync().GetAwaiter().GetResult();
        if (scheme?.Name == CookieAuthenticationDefaults.AuthenticationScheme)
        {
            logger.LogWarning(
                "Beacon's cookie scheme ({Scheme}) is the default authentication scheme, so UseAuthentication reads the Beacon.Auth cookie on every route, the host's own included. Set the host's own default scheme, or name the host's schemes on its authorization policies",
                CookieAuthenticationDefaults.AuthenticationScheme);
        }

        return next;
    }
}
