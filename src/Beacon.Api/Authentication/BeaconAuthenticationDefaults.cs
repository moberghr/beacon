using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Beacon.Core.Authentication;

namespace Beacon.Api.Authentication;

/// <summary>
/// The names of Beacon's authentication schemes, registered by <c>AddBeaconCookieAuthentication</c> and
/// <c>AddBeaconJwtAuthentication</c>. <see cref="AuthenticationScheme"/> is a policy scheme that forwards to the scheme
/// the request's credential belongs to: an <c>Authorization: Bearer sk-sem_…</c> header to <see cref="ApiKeyScheme"/>,
/// any other bearer value to <see cref="BearerScheme"/> (when bearer tokens are enabled), anything else to the
/// <c>Beacon.Auth</c> cookie. <c>UseBeaconAuthentication</c> picks the scheme the same way.
/// <para>
/// <c>UseBeaconAuthentication</c> does not pass an API key or bearer principal through the host's claims transformations,
/// as before. A host authorization policy naming one of these schemes is authenticated by the authentication service,
/// which does: a host claims transformation must leave scoped callers alone (<c>BeaconScopes.IsScopedCaller</c>).
/// </para>
/// </summary>
public static class BeaconAuthenticationDefaults
{
    /// <summary>The policy scheme that forwards to the scheme of the request's credential.</summary>
    public const string AuthenticationScheme = "Beacon";

    /// <summary>Beacon API keys (<c>Authorization: Bearer sk-sem_…</c>).</summary>
    public const string ApiKeyScheme = "Beacon.ApiKey";

    /// <summary>JWT bearer tokens, when <c>AddBeaconJwtAuthentication</c> enables them.</summary>
    public const string BearerScheme = "Beacon.Bearer";

    /// <summary>The schemes Beacon registers besides the cookie; none of them is ever a host's default.</summary>
    public static IReadOnlyList<string> RegisteredSchemes { get; } = [AuthenticationScheme, ApiKeyScheme, BearerScheme];

    /// <summary>The scheme the request's credential belongs to.</summary>
    internal static string SelectScheme(HttpContext context)
    {
        var authorization = context.Request.Headers.Authorization.FirstOrDefault();
        if (ApiKeyAuthenticator.IsApiKeyHeader(authorization))
        {
            return ApiKeyScheme;
        }

        var bearerEnabled = context.RequestServices?.GetService<JwtAuthenticationOptions>()?.EnableBearerAuthentication == true;

        return bearerEnabled && BeaconJwtAuthenticator.HasBearerToken(authorization)
            ? BearerScheme
            : CookieAuthenticationDefaults.AuthenticationScheme;
    }
}
