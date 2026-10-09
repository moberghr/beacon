using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Beacon.Core.Authentication;

namespace Beacon.Api.Authentication;

/// <summary>
/// Warns once at startup about SSO settings that are accepted but admit more than they appear to: skipping the tenant
/// check (<see cref="OidcAuthenticationOptions.AllowAnyTenant"/>) with an Entra authority, which issues tenant ids.
/// </summary>
internal sealed class OidcStartupWarnings(
    IOptions<OidcAuthenticationOptions> options,
    ILogger<OidcStartupWarnings> logger) : IStartupFilter
{
    private static readonly string[] EntraHosts =
    [
        "login.microsoftonline.com",
        "login.microsoftonline.us",
        "login.partner.microsoftonline.cn",
        "login.windows.net",
        "sts.windows.net"
    ];

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        var oidc = options.Value;
        if (oidc.Enabled && oidc.AllowAnyTenant && IsEntraAuthority(oidc.Authority))
        {
            logger.LogWarning(
                "Beacon:Authentication:Oidc:AllowAnyTenant is on with an Entra authority, so the tenant (tid) check is " +
                "skipped and every tenant the authority accepts can sign in. Set AllowedTenants to your tenant id(s) instead.");
        }

        return next;
    }

    internal static bool IsEntraAuthority(string? authority)
    {
        return Uri.TryCreate(authority?.Trim(), UriKind.Absolute, out var uri)
            && EntraHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase);
    }
}
