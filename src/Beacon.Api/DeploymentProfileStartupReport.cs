using Microsoft.Extensions.Options;
using Beacon.Core;
using Beacon.Core.Authentication;
using Beacon.Core.Configuration;

namespace Beacon.Api;

/// <summary>
/// Reports once at startup, in one entry, the settings weaker than the regulated profile requires
/// (<see cref="RegulatedProfile.Assess"/>): a Warning naming each of them while any remains, whether or not a profile is
/// set; under the regulated profile, once they all hold, an Information naming the settings the profile filled. Without a
/// profile and with nothing weak it logs nothing. Configuration names only, never a value (§1.11).
/// </summary>
internal sealed class DeploymentProfileStartupReport(
    ILogger<DeploymentProfileStartupReport> logger,
    IOptions<DeploymentProfileOptions> profileOptions,
    IOptions<McpDeploymentOptions> mcpOptions,
    IOptions<RetentionOptions> retentionOptions,
    IOptions<RateLimitOptions> rateLimitOptions,
    BeaconConfiguration? beaconConfiguration = null,
    IOptions<OidcAuthenticationOptions>? oidcOptions = null) : IStartupFilter
{
    internal static readonly EventId WeakSettingsEvent = new(9130, "DeploymentProfileWeakSettings");
    internal static readonly EventId ProfileHoldsEvent = new(9131, "DeploymentProfileHolds");

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return builder =>
        {
            Report();
            next(builder);
        };
    }

    internal void Report()
    {
        var profile = profileOptions.Value;
        var findings = RegulatedProfile.Assess(
            mcpOptions.Value,
            retentionOptions.Value,
            rateLimitOptions.Value,
            oidcOptions?.Value,
            beaconConfiguration?.UserManagement.LocalLogin ?? new LocalLoginOptions());

        if (findings.Count > 0)
        {
            logger.LogWarning(
                WeakSettingsEvent,
                "{Count} security settings are weaker than the regulated profile requires (profile: {Profile}): {Settings}. Set {ProfileKey} to Regulated to require them; see the Regulated profile section of the configuration docs",
                findings.Count,
                profile.Profile,
                string.Join("; ", findings.Select(x => $"{x.Setting} {x.Requirement}")),
                DeploymentProfileOptions.ConfigurationKey);

            return;
        }

        if (profile.IsRegulated)
        {
            logger.LogInformation(
                ProfileHoldsEvent,
                "{ProfileKey} is Regulated and every setting it requires holds; filled by the profile because the host left them unset: {Applied}",
                DeploymentProfileOptions.ConfigurationKey,
                profile.AppliedDefaults.Count == 0 ? "none" : string.Join(", ", profile.AppliedDefaults));
        }
    }
}
