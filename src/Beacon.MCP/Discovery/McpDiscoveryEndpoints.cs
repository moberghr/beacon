using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace Beacon.MCP.Discovery;

/// <summary>
/// Anonymous MCP discovery surface (tier 3):
/// <list type="bullet">
/// <item>RFC 9728 protected-resource metadata at <c>/.well-known/oauth-protected-resource</c>
/// (plus the path-inserted variant for the <c>/beacon/mcp</c> resource, which RFC 9728 §3
/// derives for resources with a path component).</item>
/// <item>Server card at <c>/.well-known/mcp/server-card.json</c> (SEP-2127, draft).</item>
/// <item><c>WWW-Authenticate: Bearer resource_metadata="…"</c> on every 401 from
/// <c>/beacon/mcp</c>, so remote clients (claude.ai, ChatGPT, VS Code) can bootstrap OAuth
/// discovery from the challenge.</item>
/// </list>
/// Hand-rolled rather than the SDK's <c>McpAuthenticationHandler</c>: the handler only injects
/// the pointer when it is the active challenge scheme, and Beacon's cookie challenge default plus
/// the scheme-agnostic Execute policy (which must honour the principal that
/// <c>ApiKeyAuthMiddleware</c> assigns outside any scheme) are both load-bearing (§1.9) —
/// see the tier-3 spec, batch D1.
/// </summary>
public static class McpDiscoveryEndpoints
{
    private const string PublicBaseUrlConfigKey = "Beacon:PublicBaseUrl";
    private const string OidcConfigSection = "Beacon:Authentication:Oidc";
    private const string CacheControlValue = "public, max-age=3600";

    public static IEndpointRouteBuilder MapMcpDiscovery(this IEndpointRouteBuilder endpoints)
    {
        // Startup-time (once, not per-request): without Beacon:PublicBaseUrl the discovery documents
        // and WWW-Authenticate challenges reflect the request Host header, which a client (or a
        // misconfigured proxy) controls. AllowedHosts defaults to *, so nothing else validates it.
        var configuration = endpoints.ServiceProvider.GetRequiredService<IConfiguration>();
        if (string.IsNullOrWhiteSpace(configuration[PublicBaseUrlConfigKey]))
        {
            var logger = endpoints.ServiceProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(typeof(McpDiscoveryEndpoints).FullName!);
            logger.LogWarning(
                "Beacon:PublicBaseUrl is not configured — MCP discovery documents and WWW-Authenticate challenges will reflect the request Host header. Set Beacon:PublicBaseUrl on public deployments so advertised URLs cannot be influenced by the caller.");
        }

        // Bare well-known path (spec'd probe target) + the RFC 9728 path-inserted variant that
        // clients derive from the /beacon/mcp resource identifier — same document from both.
        endpoints.MapGet(McpDiscoveryDocuments.ProtectedResourceMetadataPath, HandleProtectedResourceMetadata)
            .AllowAnonymous()
            .ExcludeFromDescription();
        endpoints.MapGet($"{McpDiscoveryDocuments.ProtectedResourceMetadataPath}{McpDiscoveryDocuments.McpPath}", HandleProtectedResourceMetadata)
            .AllowAnonymous()
            .ExcludeFromDescription();
        endpoints.MapGet(McpDiscoveryDocuments.ServerCardPath, HandleServerCard)
            .AllowAnonymous()
            .ExcludeFromDescription();

        return endpoints;
    }

    /// <summary>
    /// Decorates 401 responses on the MCP route with the RFC 9728 <c>resource_metadata</c>
    /// challenge parameter. Response-header-only: it never authenticates, never short-circuits,
    /// and leaves the §1.9 middleware order untouched. Registered before
    /// <c>ApiKeyAuthMiddleware</c> so the <c>OnStarting</c> hook also covers the 401s that the
    /// API-key and JWT middlewares write directly (they short-circuit before any challenge runs).
    /// </summary>
    public static IApplicationBuilder UseMcpResourceMetadataChallenge(this IApplicationBuilder app)
    {
        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(McpDiscoveryDocuments.McpPath))
            {
                context.Response.OnStarting(() =>
                {
                    if (context.Response.StatusCode == StatusCodes.Status401Unauthorized)
                    {
                        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
                        var baseUrl = ResolvePublicBaseUrl(context.Request, configuration);
                        var metadataUrl = $"{baseUrl}{McpDiscoveryDocuments.ProtectedResourceMetadataPath}";
                        var challenge = BuildResourceMetadataChallenge(
                            context.Response.Headers[HeaderNames.WWWAuthenticate].ToString(), metadataUrl);
                        if (challenge != null)
                        {
                            context.Response.Headers.WWWAuthenticate = challenge;
                        }
                    }

                    return Task.CompletedTask;
                });
            }

            await next(context);
        });
    }

    /// <summary>
    /// Computes the <c>WWW-Authenticate</c> value that carries the RFC 9728 <c>resource_metadata</c>
    /// parameter, or null when the response header must stay untouched. Rules:
    /// no existing header → a fresh <c>Bearer resource_metadata="…"</c> challenge; an existing Bearer
    /// challenge without the parameter (JwtBearerAuthMiddleware writes a bare <c>Bearer</c> on invalid
    /// tokens) → the parameter is appended, preserving any existing auth-params; a challenge already
    /// carrying <c>resource_metadata</c> or using a non-Bearer scheme → untouched (null).
    /// Internal for unit tests (InternalsVisibleTo Beacon.Tests).
    /// </summary>
    internal static string? BuildResourceMetadataChallenge(string? existingValue, string metadataUrl)
    {
        var parameter = $"resource_metadata=\"{metadataUrl}\"";
        if (string.IsNullOrWhiteSpace(existingValue))
        {
            return $"Bearer {parameter}";
        }

        if (existingValue.Contains("resource_metadata", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var trimmed = existingValue.Trim();
        if (trimmed.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            return $"Bearer {parameter}";
        }

        if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return $"{trimmed}, {parameter}";
        }

        return null;
    }

    private static IResult HandleProtectedResourceMetadata(HttpContext context, IConfiguration configuration)
    {
        var baseUrl = ResolvePublicBaseUrl(context.Request, configuration);

        var oidcSection = configuration.GetSection(OidcConfigSection);
        var oidcAuthority = oidcSection.GetValue<bool>("Enabled")
            ? oidcSection.GetValue<string>("Authority")?.TrimEnd('/')
            : null;

        var document = McpDiscoveryDocuments.BuildProtectedResourceMetadata(baseUrl, oidcAuthority);
        context.Response.Headers.CacheControl = CacheControlValue;
        return Results.Text(document.ToJsonString(), "application/json");
    }

    private static IResult HandleServerCard(HttpContext context, IConfiguration configuration)
    {
        var baseUrl = ResolvePublicBaseUrl(context.Request, configuration);

        var document = McpDiscoveryDocuments.BuildServerCard(baseUrl);
        context.Response.Headers.CacheControl = CacheControlValue;
        return Results.Text(document.ToJsonString(), "application/json");
    }

    private static string ResolvePublicBaseUrl(HttpRequest request, IConfiguration configuration)
    {
        var configured = configuration[PublicBaseUrlConfigKey];
        return string.IsNullOrWhiteSpace(configured)
            ? $"{request.Scheme}://{request.Host}"
            : configured.TrimEnd('/');
    }
}
