using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace Beacon.Api.Authentication;

/// <summary>
/// Warns once, after the host has started and outside Development, when the server listens on a plain HTTP address while
/// <c>Beacon:Authentication:Cookie:SecureAlways</c> is off: the session cookie's Secure flag then follows the request's
/// scheme, so behind a TLS-terminating proxy it is issued without the flag unless forwarded headers restore the scheme.
/// Skipped when <see cref="ForwardedHeadersOptions"/> already honours <c>X-Forwarded-Proto</c>, the usual proxied setup.
/// Configuration names and listen schemes only.
/// </summary>
internal sealed class CookieTransportStartupWarning(
    ILogger<CookieTransportStartupWarning> logger,
    IHostEnvironment? environment = null,
    IConfiguration? configuration = null) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        if (environment == null || environment.IsDevelopment() || BeaconCookieSecurity.IsSecureAlways(configuration))
        {
            return next;
        }

        return builder =>
        {
            var lifetime = builder.ApplicationServices.GetService<IHostApplicationLifetime>();
            lifetime?.ApplicationStarted.Register(() => WarnWhenPlainHttp(builder.ApplicationServices));

            next(builder);
        };
    }

    internal void WarnWhenPlainHttp(IServiceProvider services)
    {
        var forwarded = services.GetService<IOptions<ForwardedHeadersOptions>>()?.Value.ForwardedHeaders ?? ForwardedHeaders.None;
        if (forwarded.HasFlag(ForwardedHeaders.XForwardedProto))
        {
            return;
        }

        var addresses = services.GetService<IServer>()?.Features.Get<IServerAddressesFeature>()?.Addresses;
        if (addresses == null || !addresses.Any(x => x.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        logger.LogWarning(
            "The server listens on plain HTTP outside Development, no forwarded scheme is configured and {Key} is off: if Beacon is reached over plain HTTP, the Beacon.Auth session cookie is issued without the Secure flag (the option covers that cookie only). Behind a TLS-terminating proxy, honour the forwarded scheme (UseForwardedHeaders with the proxy as a known proxy), or set {Key} to true, and add HTTPS redirection and HSTS",
            BeaconCookieSecurity.SecureAlwaysKey,
            BeaconCookieSecurity.SecureAlwaysKey);
    }
}
