using Beacon.Api.Authentication;
using Beacon.Core.Configuration;
using Microsoft.Extensions.Options;

namespace Beacon.Api.Security;

/// <summary>
/// The security response headers on Beacon's routes (<see cref="BeaconRequestPaths"/>): <c>X-Content-Type-Options</c>,
/// <c>X-Frame-Options</c>, <c>Referrer-Policy</c> and <c>Permissions-Policy</c>, and on <c>/beacon</c> the
/// Content-Security-Policy in the configured mode. A header the response already carries (set by the host or an endpoint)
/// is kept. Written when the response starts, so error pages and static files served after it carry them too.
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next, IOptions<BeaconHostSecurityOptions> options)
{
    /// <summary>
    /// What the shipped SPA needs: its scripts, styles, fonts and workers come from <c>/beacon/assets</c> and its index
    /// has no inline script; styles allow <c>'unsafe-inline'</c> because the editor and the diagram renderer inject style
    /// elements; images allow <c>data:</c> (editor icons). Nothing is framed, posted or loaded from another origin.
    /// </summary>
    internal const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; "
        + "connect-src 'self'; worker-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'";

    // strict-origin, not no-referrer: under no-referrer a browser sends "Origin: null" on the SPA's own POSTs.
    internal const string ReferrerPolicy = "strict-origin";

    internal const string PermissionsPolicy =
        "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";

    private readonly BeaconCspMode _cspMode = options.Value.Csp;

    public Task InvokeAsync(HttpContext context)
    {
        if (BeaconRequestPaths.IsBeaconRequest(context))
        {
            var cspMode = context.Request.Path.StartsWithSegments(AuthenticationPathOptions.BeaconPath, StringComparison.OrdinalIgnoreCase)
                ? _cspMode
                : BeaconCspMode.Off;
            context.Response.OnStarting(
                static state =>
                {
                    var (response, mode) = ((HttpResponse, BeaconCspMode))state;
                    Apply(response.Headers, mode);
                    return Task.CompletedTask;
                },
                (context.Response, cspMode));
        }

        return next(context);
    }

    internal static void Apply(IHeaderDictionary headers, BeaconCspMode cspMode)
    {
        headers.TryAdd("X-Content-Type-Options", "nosniff");
        headers.TryAdd("X-Frame-Options", "DENY");
        headers.TryAdd("Referrer-Policy", ReferrerPolicy);
        headers.TryAdd("Permissions-Policy", PermissionsPolicy);

        // A policy the host or the endpoint sent, in either form, is theirs.
        if (cspMode == BeaconCspMode.Off
            || headers.ContainsKey("Content-Security-Policy")
            || headers.ContainsKey("Content-Security-Policy-Report-Only"))
        {
            return;
        }

        var name = cspMode == BeaconCspMode.Enforce
            ? "Content-Security-Policy"
            : "Content-Security-Policy-Report-Only";
        headers[name] = ContentSecurityPolicy;
    }
}
