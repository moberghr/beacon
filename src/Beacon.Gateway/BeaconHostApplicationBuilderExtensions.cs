using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Beacon.Api;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.Configuration;
using Beacon.MCP;
using Beacon.MCP.Discovery;
using Beacon.UI;

namespace Beacon.Gateway;

/// <summary>
/// The request side of the host chain <c>services.AddBeacon(...)</c> starts: <see cref="UseBeaconPipeline"/> adds Beacon's
/// middlewares in the order they need, <see cref="MapBeacon"/> maps Beacon's endpoints. Both serve what the chain
/// registered and nothing else. They live in this package because it is the one that composes the API, the MCP server
/// and the shell; a host embedding Beacon as an MCP gateway uses <c>UseBeaconMcpGateway</c> and <c>MapBeaconMcpGateway</c>
/// instead, and calling these on it throws.
/// </summary>
public static class BeaconHostApplicationBuilderExtensions
{
    internal const string PipelineStagesKey = "beacon:pipeline-stages";

    /// <summary>
    /// Adds Beacon's middlewares in their load-bearing order, in place of the host's <c>UseAuthentication()</c> and
    /// <c>UseAuthorization()</c>: the RFC 9728 challenge pointer on <c>/beacon/mcp</c> 401s (with <c>.AddMcp()</c>),
    /// <c>UseBeaconAuthentication</c> (API key, bearer JWT, <c>UseAuthentication</c>, the <c>Beacon.Auth</c> cookie),
    /// <c>UseAuthorization</c>, the login-form redirect (with <c>Authentication.EnableLoginForm</c>), the
    /// first-run setup redirect (with user management) and antiforgery (with <c>.AddApi()</c>). Call it once, after
    /// <c>UseStaticFiles</c>; a second call throws, because Beacon's middlewares must run once and in this order.
    /// </summary>
    public static IApplicationBuilder UseBeaconPipeline(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var services = app.ApplicationServices;
        RefuseOnGateway(services, nameof(UseBeaconPipeline), "UseBeaconMcpGateway");
        if (app.Properties.ContainsKey(PipelineStagesKey))
        {
            throw new InvalidOperationException(
                "UseBeaconPipeline() has already been called on this application. Beacon's middlewares must run once, in one place, in the order it "
                + "sets: remove the second call (and any UseBeaconAuthentication, UseAuthorization or Beacon middleware the host adds beside it).");
        }

        var stages = new List<string>();
        app.Properties[PipelineStagesKey] = stages;
        var configuration = services.GetRequiredService<BeaconConfiguration>();

        if (services.GetService<BeaconMcpOptions>() != null)
        {
            app.UseMcpResourceMetadataChallenge();
            stages.Add(nameof(McpDiscoveryEndpoints.UseMcpResourceMetadataChallenge));
        }

        app.UseBeaconAuthentication();
        stages.Add(nameof(AuthServiceExtensions.UseBeaconAuthentication));

        app.UseAuthorization();
        stages.Add(nameof(AuthorizationAppBuilderExtensions.UseAuthorization));

        if (configuration.Authentication.EnableLoginForm)
        {
            app.UseMiddleware<LoginFormAuthMiddleware>(configuration, AuthenticationPathOptions.BeaconPath);
            stages.Add(nameof(LoginFormAuthMiddleware));
        }

        if (configuration.UserManagement.Enabled)
        {
            app.UseMiddleware<FirstRunSetupMiddleware>(configuration, AuthenticationPathOptions.BeaconPath);
            stages.Add(nameof(FirstRunSetupMiddleware));
        }

        // After authentication, so the token is issued for the signed-in user.
        if (services.GetService<IAntiforgery>() != null)
        {
            app.UseAntiforgery();
            stages.Add(nameof(AntiforgeryApplicationBuilderExtensions.UseAntiforgery));
        }

        return app;
    }

    /// <summary>
    /// Maps Beacon's endpoints: with <c>.AddApi()</c> the REST API, the sign-in endpoints (with <c>.AddCookieAuth()</c>), the
    /// first-run setup endpoints (with user management) and the React shell at <c>/beacon</c>; with <c>.AddMcp()</c> the MCP server at
    /// <c>/beacon/mcp</c> under the Execute-scope policy and the anonymous discovery documents.
    /// </summary>
    public static WebApplication MapBeacon(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        RefuseOnGateway(app.Services, nameof(MapBeacon), "MapBeaconMcpGateway");
        var configuration = app.Services.GetRequiredService<BeaconConfiguration>();
        var api = app.Services.GetService<BeaconApiOptions>() != null;

        if (api)
        {
            app.MapBeaconApi();

            // Sign-in issues the Beacon.Auth cookie: mapped only when the cookie scheme is registered (.AddCookieAuth()).
            if (app.Services.GetService<IServiceProviderIsService>()?.IsService(typeof(IBeaconAuthenticationService)) == true)
            {
                app.MapLoginEndpoints(AuthenticationPathOptions.BeaconPath, configuration);
            }

            if (configuration.UserManagement.Enabled)
            {
                app.MapSetupEndpoints(AuthenticationPathOptions.BeaconPath);
            }
        }

        if (app.Services.GetService<BeaconMcpOptions>() != null)
        {
            app.MapBeaconMcp();
            app.MapMcpDiscovery();
        }

        if (api)
        {
            app.MapBeaconUi();
        }

        return app;
    }

    /// <summary>The stages <see cref="UseBeaconPipeline"/> added to <paramref name="app"/>, in order; empty before it ran.</summary>
    internal static IReadOnlyList<string> PipelineStages(IApplicationBuilder app)
    {
        return app.Properties.TryGetValue(PipelineStagesKey, out var stages) && stages is List<string> list ? list : [];
    }

    private static void RefuseOnGateway(IServiceProvider services, string method, string gatewayMethod)
    {
        if (services.GetService<BeaconMcpGatewayOptions>() != null)
        {
            throw new InvalidOperationException(
                $"{method}() serves a host built with services.AddBeacon(...); this host embeds Beacon as an MCP gateway (AddBeaconMcpGateway): call {gatewayMethod}() instead.");
        }
    }
}
