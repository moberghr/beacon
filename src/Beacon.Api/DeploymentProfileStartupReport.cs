using Microsoft.Extensions.Options;
using Beacon.Core.Configuration;

namespace Beacon.Api;

/// <summary>
/// Reports once at startup the settings weaker than the regulated profile requires
/// (<see cref="RegulatedProfile.Assess(RegulatedProfileSettings)"/>): under the profile, a Warning naming each of them while
/// any remains, and once they all hold an Information naming the settings the profile filled, and another naming the
/// surfaces it allows that are on (the embed routes, data migration). Without a profile, the Warning names only the
/// settings the report has always judged (<see cref="RegulatedProfile.OriginalSettings"/>), so an alert on it does not
/// start firing at an upgrade, and one Information names the other weak settings; with nothing weak it logs nothing.
/// Configuration names only, never a value (§1.11).
/// </summary>
internal sealed class DeploymentProfileStartupReport(
    ILogger<DeploymentProfileStartupReport> logger,
    IOptions<DeploymentProfileOptions> profileOptions,
    IServiceProvider services) : IStartupFilter
{
    internal static readonly EventId WeakSettingsEvent = new(9130, "DeploymentProfileWeakSettings");
    internal static readonly EventId ProfileHoldsEvent = new(9131, "DeploymentProfileHolds");
    internal static readonly EventId SurfacesOnEvent = new(9132, "DeploymentProfileSurfacesOn");
    internal static readonly EventId OtherWeakSettingsEvent = new(9133, "DeploymentProfileOtherWeakSettings");

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return builder =>
        {
            var configuration = services.GetService<IConfiguration>() ?? new ConfigurationBuilder().Build();
            Report(RegulatedProfileSettings.From(services, configuration));
            next(builder);
        };
    }

    internal void Report(RegulatedProfileSettings settings)
    {
        var profile = profileOptions.Value;
        var findings = RegulatedProfile.Assess(settings);

        if (findings.Count > 0)
        {
            ReportWeak(profile, findings);

            return;
        }

        if (!profile.IsRegulated)
        {
            return;
        }

        logger.LogInformation(
            ProfileHoldsEvent,
            "{ProfileKey} is Regulated and every setting it requires holds; filled by the profile because the host left them unset: {Applied}",
            DeploymentProfileOptions.ConfigurationKey,
            profile.AppliedDefaults.Count == 0 ? "none" : string.Join(", ", profile.AppliedDefaults));

        var surfaces = RegulatedProfile.EnabledSurfaces(settings);
        if (surfaces.Count > 0)
        {
            logger.LogInformation(
                SurfacesOnEvent,
                "{ProfileKey} is Regulated and these surfaces are on; review that the deployment needs them: {Surfaces}",
                DeploymentProfileOptions.ConfigurationKey,
                string.Join(", ", surfaces));
        }
    }

    private void ReportWeak(DeploymentProfileOptions profile, IReadOnlyList<DeploymentProfileFinding> findings)
    {
        var warned = findings
            .Where(x => profile.IsRegulated || RegulatedProfile.OriginalSettings.Contains(x.Setting))
            .ToList();
        var others = findings
            .Where(x => !warned.Contains(x))
            .ToList();

        if (warned.Count > 0)
        {
            logger.LogWarning(
                WeakSettingsEvent,
                "{Count} security settings are weaker than the regulated profile requires (profile: {Profile}): {Settings}. Set {ProfileKey} to Regulated to require them; see the Regulated profile section of the configuration docs",
                warned.Count,
                profile.Profile,
                string.Join("; ", warned.Select(x => $"{x.Setting} {x.Requirement}")),
                DeploymentProfileOptions.ConfigurationKey);
        }

        if (others.Count > 0)
        {
            logger.LogInformation(
                OtherWeakSettingsEvent,
                "{Count} other settings are weaker than the regulated profile requires (profile: {Profile}): {Settings}. {ProfileKey} set to Regulated requires them; see the Regulated profile section of the configuration docs",
                others.Count,
                profile.Profile,
                string.Join(", ", others.Select(x => x.Setting)),
                DeploymentProfileOptions.ConfigurationKey);
        }
    }
}
