using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Beacon.Core.Authentication;

namespace Beacon.Core.Configuration;

/// <summary>A preset of security settings a host opts into with <c>Beacon:Profile</c>.</summary>
public enum DeploymentProfile
{
    /// <summary>No profile (<c>Beacon:Profile</c> unset): every setting keeps its own default.</summary>
    None = 0,

    /// <summary>
    /// The settings a regulated deployment needs (<see cref="RegulatedProfile"/>): the preset fills the ones the host left
    /// unset and fails startup, with the full list, on any the host set to a weaker value.
    /// </summary>
    Regulated = 1
}

/// <summary>
/// The deployment profile, read from <c>Beacon:Profile</c> at startup. Unset, nothing changes; <c>Regulated</c> applies and
/// validates the regulated preset (<see cref="RegulatedProfile"/>); <c>None</c> is the same as unset. Any other value fails startup.
/// </summary>
public sealed class DeploymentProfileOptions
{
    public const string ConfigurationKey = "Beacon:Profile";

    public DeploymentProfile Profile { get; set; }

    /// <summary>The settings the profile filled because the host left them unset, by configuration key.</summary>
    public IReadOnlyList<string> AppliedDefaults { get; set; } = [];

    /// <summary>The configured value when it names no profile; such a value fails startup.</summary>
    internal string? UnknownValue { get; set; }

    public bool IsRegulated => Profile == DeploymentProfile.Regulated;

    /// <summary>Reads the profile from <c>Beacon:Profile</c> by name (case-insensitive); unset or blank is <see cref="DeploymentProfile.None"/>.</summary>
    public static DeploymentProfileOptions From(IConfiguration configuration)
    {
        var value = configuration[ConfigurationKey]?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return new DeploymentProfileOptions();
        }

        // Only the names: a number would parse as an enum value too.
        var known = Enum.GetNames<DeploymentProfile>()
            .FirstOrDefault(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase));

        if (known == null)
        {
            return new DeploymentProfileOptions { UnknownValue = value };
        }

        var profile = Enum.Parse<DeploymentProfile>(known);

        return new DeploymentProfileOptions
        {
            Profile = profile,
            AppliedDefaults = profile == DeploymentProfile.Regulated ? RegulatedProfile.UnsetDefaults(configuration) : []
        };
    }
}

/// <summary>
/// Fails startup for an unknown <c>Beacon:Profile</c>, and under the regulated profile for every setting that is weaker
/// than the preset requires (<see cref="RegulatedProfile.Assess"/>), all of them in one message. Configuration names only,
/// never a value from a secret.
/// </summary>
internal sealed class DeploymentProfileOptionsValidator(
    IOptions<McpDeploymentOptions> mcpOptions,
    IOptions<RetentionOptions> retentionOptions,
    IOptions<RateLimitOptions> rateLimitOptions,
    IOptions<OidcAuthenticationOptions> oidcOptions,
    BeaconConfiguration beaconConfiguration) : IValidateOptions<DeploymentProfileOptions>
{
    public ValidateOptionsResult Validate(string? name, DeploymentProfileOptions options)
    {
        if (options.UnknownValue != null)
        {
            return ValidateOptionsResult.Fail($"{DeploymentProfileOptions.ConfigurationKey} names no profile; known profiles: {string.Join(", ", Enum.GetNames<DeploymentProfile>())} (or leave it unset).");
        }

        if (!options.IsRegulated)
        {
            return ValidateOptionsResult.Success;
        }

        var findings = RegulatedProfile.Assess(
            mcpOptions.Value,
            retentionOptions.Value,
            rateLimitOptions.Value,
            oidcOptions.Value,
            beaconConfiguration.UserManagement.LocalLogin);

        return findings.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(findings.Select(x => $"{DeploymentProfileOptions.ConfigurationKey} is {nameof(DeploymentProfile.Regulated)}, so {x.Setting} {x.Requirement}."));
    }
}
