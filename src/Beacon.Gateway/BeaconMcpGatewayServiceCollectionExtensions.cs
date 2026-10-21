using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Beacon.AI;
using Beacon.Api;
using Beacon.Api.Authentication;
using Beacon.Core;
using Beacon.Core.Configuration;
using Beacon.Core.HostData;
using Beacon.Core.Mcp;
using Beacon.MCP;
using Beacon.MCP.HostEndpoints;

namespace Beacon.Gateway;

public static class BeaconMcpGatewayServiceCollectionExtensions
{
    /// <summary>
    /// Embeds Beacon as the MCP gateway of the host application. Composes Beacon for it — and nothing else of Beacon:
    /// <list type="bullet">
    /// <item>Callers are the host's own users: delegated access tokens of the host's Entra registration
    /// (<see cref="BeaconMcpGatewayOptions.Entra(IConfiguration)"/>) whose person the host's
    /// <see cref="IMcpHostUserResolver"/> knows (<see cref="HostUserMcpCallerMapper"/>). No system callers, no API keys,
    /// no Beacon users or sign-in.</item>
    /// <item>Tools are the host's endpoints marked <c>[BeaconTool]</c> (<see cref="BeaconMcpGatewayOptions.Tools"/>), run as
    /// the user (<see cref="HostUserMcpHostPrincipalFactory"/>), plus the document tools; no SQL tools, no saved-query
    /// tools, no data sources.</item>
    /// <item>Read-only is pinned, and so is no content retention unless <see cref="BeaconMcpGatewayOptions.RetainToolContent"/>.</item>
    /// <item>The discovery documents publish the tenant's v2 issuer and <c>api://&lt;clientId&gt;/&lt;scope&gt;</c> plus
    /// <c>offline_access</c>, unless <c>Beacon:Mcp:ProtectedResource</c> sets them.</item>
    /// <item>The host project is synced the first time it is needed, and a <c>beacon-mcp-gateway</c> health check reports
    /// Degraded until it is.</item>
    /// </list>
    /// Beacon needs <c>Beacon:EncryptionKey</c> and its own schema in a database (<see cref="BeaconMcpGatewayOptions.Database"/>;
    /// migrate it with <c>BeaconPostgreSqlMigrator</c> or <c>BeaconSqlServerMigrator</c> from the host's migration step). Then
    /// call <see cref="BeaconMcpGatewayApplicationBuilderExtensions.UseBeaconMcpGateway"/> before <c>UseAuthentication</c>
    /// and <see cref="BeaconMcpGatewayApplicationBuilderExtensions.MapBeaconMcpGateway"/>.
    /// </summary>
    public static IServiceCollection AddBeaconMcpGateway(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<BeaconMcpGatewayOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configure);

        if (services.Any(x => x.ServiceType == typeof(BeaconMcpGatewayOptions)))
        {
            throw new InvalidOperationException("AddBeaconMcpGateway can only be called once.");
        }

        var options = new BeaconMcpGatewayOptions();
        configure(options);
        options.Validate();
        services.AddSingleton(options);
        var entra = options.EntraConfiguration!;

        // Beacon's user store exists (the admin UI provisions its administrators in it) but holds no password and has no
        // first-run setup: the host owns sign-in. No scheduler: a gateway has no subscriptions or data sources to schedule.
        var beacon = services.AddBeaconServices(configuration, x =>
        {
            x.AddBeaconScheduler<UnavailableBeaconScheduler>();
            x.BaseUrl = configuration["Beacon:PublicBaseUrl"];
            x.UserManagement = new UserManagementOptions
            {
                Enabled = true,
                AllowInternalUsers = false,
                HostOwnsSignIn = true
            };
        });
        foreach (var docs in options.DocsConfigurations)
        {
            beacon.ExposeDocs(x =>
            {
                x.ProjectName = options.ProjectName;
                docs(x);
            });
        }

        options.DatabaseConfiguration!(beacon);

        // Pinned in code, whatever a configuration row or a Beacon administrator says: a gateway runs no SQL, and a tool's
        // input and output can carry anything the host returns.
        services.PostConfigure<McpDeploymentOptions>(x =>
        {
            x.ForceReadOnly = true;
            if (!options.RetainToolContent)
            {
                x.ForceNoContentRetention = true;
            }
        });

        // The MCP tools' hard dependency; no LLM provider is configured, the client brings its own model.
        services.AddBeaconAI(configuration);
        services.AddBeaconApiServices(x => x.Realtime = false);

        services.AddBeaconJwtAuthentication(x =>
        {
            x.EnableBearerAuthentication = true;
            x.Validation.JwksEndpoint = entra.JwksEndpoint;
            x.Validation.ValidIssuers = [.. entra.Issuers];
            x.Validation.ValidAudiences = [.. entra.Audiences];
            x.Validation.ValidateIssuer = true;
            x.Validation.ValidateAudience = true;
        });

        services.AddBeaconMcp(x =>
        {
            x.BuiltInTools.Clear();
            x.BuiltInTools.UnionWith(options.BuiltInTools);
            x.SavedQueryTools = false;
            x.ServerInstructions = options.ServerInstructions;
        });
        services.AddHostEndpointTools(x =>
        {
            x.ProjectName = options.ProjectName;
            foreach (var tools in options.ToolConfigurations)
            {
                tools(x);
            }
        });

        // Who a token is, and what a tool call runs as: the host's users. Replace, so Beacon's configuration-driven defaults
        // cannot win whichever registration ran first.
        services.AddOptions<HostUserMcpCallerOptions>().Configure(x =>
        {
            x.TenantId = entra.TenantId;
            x.RequiredScope = entra.Scope;
            x.AllowedClientIds = [.. entra.AllowedClientIds];
            x.ProjectName = options.ProjectName;
        });
        services.AddScoped(typeof(IMcpHostUserResolver), options.UserResolverType!);
        services.Replace(ServiceDescriptor.Scoped<IMcpCallerMapper, HostUserMcpCallerMapper>());
        services.Replace(ServiceDescriptor.Singleton<IMcpHostPrincipalFactory, HostUserMcpHostPrincipalFactory>());
        services.AddBeaconHostProjectGate(x => x.ProjectName = options.ProjectName);

        // What an MCP client (Claude's connector) is told about signing in, for each setting configuration leaves unset.
        services.PostConfigure<McpProtectedResourceOptions>(x =>
        {
            if (x.AuthorizationServers.Count == 0)
            {
                x.DeriveAuthorizationServerFromJwtIssuer = true;
            }

            if (x.ScopesSupported.Count == 0)
            {
                x.ScopesSupported = [entra.QualifiedScope, McpProtectedResourceOptions.OfflineAccessScope];
            }
        });

        // The MCP route is decided by the MCP credential alone: its policy names this scheme, so a host cookie never counts
        // there, and an unauthenticated or refused caller gets 401/403, never the host's sign-in redirect.
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, BeaconMcpAuthenticationHandler>(BeaconMcpGatewayDefaults.AuthenticationScheme, _ => { });
        services.TryAddEnumerable(ServiceDescriptor.Transient<IStartupFilter, BeaconMcpGatewayStartupCheck>());

        services.AddHealthChecks()
            .AddCheck<BeaconMcpGatewayHealthCheck>(BeaconMcpGatewayDefaults.HealthCheckName, HealthStatus.Degraded);

        return services;
    }
}

/// <summary>Names the MCP gateway registers.</summary>
public static class BeaconMcpGatewayDefaults
{
    /// <summary>The authentication scheme of the MCP route.</summary>
    public const string AuthenticationScheme = "BeaconMcp";

    /// <summary>The health check that reports whether the host project has been synced.</summary>
    public const string HealthCheckName = "beacon-mcp-gateway";
}
