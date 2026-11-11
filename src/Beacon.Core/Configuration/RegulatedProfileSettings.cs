using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Beacon.Core.Authentication;
using Beacon.Core.Services.Embed;

namespace Beacon.Core.Configuration;

/// <summary>
/// The settings the regulated profile judges (<see cref="RegulatedProfile.Assess(RegulatedProfileSettings)"/>), as the host
/// resolved them: Beacon's bound options, its code configuration (<see cref="Beacon"/>) and the host's configuration, for the
/// keys Beacon reads from it directly. Each one left out is Beacon's own default.
/// </summary>
public sealed class RegulatedProfileSettings
{
    public McpDeploymentOptions Mcp { get; init; } = new();

    public RetentionOptions Retention { get; init; } = new();

    public RateLimitOptions RateLimits { get; init; } = new();

    /// <summary>Single sign-on; null when the host does not offer it.</summary>
    public OidcAuthenticationOptions? Oidc { get; init; }

    /// <summary>The code configuration: user management, its local login and password length, the approval workflow.</summary>
    public BeaconConfiguration Beacon { get; init; } = new();

    public SecurityAuditOptions Audit { get; init; } = new();

    public DataSourceTransportOptions Transport { get; init; } = new();

    public DataSourceConnectionOptions DataSources { get; init; } = new();

    public DataSourceOutboundOptions Outbound { get; init; } = new();

    public LlmEndpointOptions Llm { get; init; } = new();

    public ProjectAuthorizationOptions ProjectAuthorization { get; init; } = new();

    public ApiKeyOptions ApiKeys { get; init; } = new();

    public EmbedOptions Embed { get; init; } = new();

    public MigrationOptions Migrations { get; init; } = new();

    /// <summary>
    /// The host's configuration: the profile itself and the keys Beacon reads from it directly (the public base URL, the
    /// security headers and origin check, the session cookie, Data Protection and the legacy encryption format).
    /// </summary>
    public IConfiguration Configuration { get; init; } = new ConfigurationBuilder().Build();

    /// <summary>The settings as <paramref name="services"/> resolve them, with <paramref name="configuration"/> for the keys read directly.</summary>
    internal static RegulatedProfileSettings From(IServiceProvider services, IConfiguration configuration)
    {
        return new RegulatedProfileSettings
        {
            Mcp = Value<McpDeploymentOptions>(services),
            Retention = Value<RetentionOptions>(services),
            RateLimits = Value<RateLimitOptions>(services),
            Oidc = services.GetService<IOptions<OidcAuthenticationOptions>>()?.Value,
            Beacon = services.GetService<BeaconConfiguration>() ?? new BeaconConfiguration(),
            Audit = Value<SecurityAuditOptions>(services),
            Transport = Value<DataSourceTransportOptions>(services),
            DataSources = Value<DataSourceConnectionOptions>(services),
            Outbound = Value<DataSourceOutboundOptions>(services),
            Llm = Value<LlmEndpointOptions>(services),
            ProjectAuthorization = Value<ProjectAuthorizationOptions>(services),
            ApiKeys = Value<ApiKeyOptions>(services),
            Embed = Value<EmbedOptions>(services),
            Migrations = Value<MigrationOptions>(services),
            Configuration = configuration
        };
    }

    private static T Value<T>(IServiceProvider services) where T : class, new()
    {
        return services.GetService<IOptions<T>>()?.Value ?? new T();
    }
}
