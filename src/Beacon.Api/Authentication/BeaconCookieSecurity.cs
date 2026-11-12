using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;

namespace Beacon.Api.Authentication;

/// <summary>
/// Reads <c>Beacon:Authentication:Cookie:SecureAlways</c> (default false; true under the regulated profile when unset). Off,
/// the session cookie's Secure flag follows the request's scheme (<see cref="CookieSecurePolicy.SameAsRequest"/>); on, the
/// cookie is always issued with the Secure flag, whatever scheme the request appears to have.
/// </summary>
internal static class BeaconCookieSecurity
{
    public const string SecureAlwaysKey = RegulatedProfile.CookieSecureAlwaysKey;

    public static bool IsSecureAlways(IConfiguration? configuration)
    {
        if (configuration == null)
        {
            return false;
        }

        return RegulatedProfile.Fills(configuration, SecureAlwaysKey) || configuration.GetValue<bool>(SecureAlwaysKey);
    }
}

internal sealed class BeaconCookieSecurePolicyConfigurator(IConfiguration? configuration = null)
    : IConfigureNamedOptions<CookieAuthenticationOptions>
{
    public void Configure(string? name, CookieAuthenticationOptions options)
    {
        if (name == CookieAuthenticationDefaults.AuthenticationScheme && BeaconCookieSecurity.IsSecureAlways(configuration))
        {
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        }
    }

    public void Configure(CookieAuthenticationOptions options)
    {
        Configure(Options.DefaultName, options);
    }
}
