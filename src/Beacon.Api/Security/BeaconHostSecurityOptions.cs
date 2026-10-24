using Microsoft.Extensions.Options;

namespace Beacon.Api.Security;

/// <summary>How Beacon sends its Content-Security-Policy (<c>Beacon:Security:Csp</c>).</summary>
public enum BeaconCspMode
{
    /// <summary><c>Content-Security-Policy</c>: the browser blocks what the policy does not allow. The default.</summary>
    Enforce,

    /// <summary><c>Content-Security-Policy-Report-Only</c>: the browser reports violations in its console and blocks nothing.</summary>
    ReportOnly,

    /// <summary>No policy header; the other security headers are still sent.</summary>
    Off
}

/// <summary>What the origin check does with a request it refuses (<c>Beacon:Security:OriginGuard</c>).</summary>
public enum BeaconOriginGuardMode
{
    /// <summary>The request goes on and a warning is logged. The default for this release.</summary>
    LogOnly,

    /// <summary>The request is answered 403.</summary>
    Enforce
}

/// <summary>
/// The browser-facing protections on Beacon's routes, bound from <c>Beacon:Security</c>: the security response headers
/// (<see cref="SecurityHeadersMiddleware"/>) and the Host and Origin check (<see cref="OriginGuardMiddleware"/>). Both act on
/// Beacon's routes only (<c>/beacon</c> and <c>Beacon:Authentication:AdditionalPaths</c>); the Content-Security-Policy is
/// sent on <c>/beacon</c> alone. HSTS stays the host's (<c>UseHsts</c>), since it binds the whole host name.
/// </summary>
public sealed class BeaconHostSecurityOptions
{
    public const string SectionName = "Beacon:Security";

    /// <summary>The Content-Security-Policy mode on <c>/beacon</c> responses. Default <see cref="BeaconCspMode.Enforce"/>.</summary>
    public BeaconCspMode Csp { get; set; } = BeaconCspMode.Enforce;

    /// <summary>Whether a refused origin is answered 403 or only logged. Default <see cref="BeaconOriginGuardMode.LogOnly"/>.</summary>
    public BeaconOriginGuardMode OriginGuard { get; set; } = BeaconOriginGuardMode.LogOnly;

    /// <summary>
    /// The origins browsers reach Beacon from, such as <c>https://beacon.example.com</c>: scheme, host and an optional port,
    /// no path. Together with <c>Beacon:PublicBaseUrl</c> they replace the default, which is the request's own origin (its
    /// scheme and Host, after the host's forwarded headers).
    /// </summary>
    public List<string> AllowedOrigins { get; set; } = [];
}

internal sealed class BeaconHostSecurityOptionsValidator : IValidateOptions<BeaconHostSecurityOptions>
{
    public ValidateOptionsResult Validate(string? name, BeaconHostSecurityOptions options)
    {
        var failures = new List<string>();
        for (var index = 0; index < options.AllowedOrigins.Count; index++)
        {
            if (BeaconOrigin.TryParseConfigured(options.AllowedOrigins[index]) == null)
            {
                failures.Add(
                    $"{BeaconHostSecurityOptions.SectionName}:{nameof(BeaconHostSecurityOptions.AllowedOrigins)}:{index} must be an http or https origin such as https://beacon.example.com: a scheme, a host and an optional port, without a path, query or fragment.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
