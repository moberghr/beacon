using Beacon.Core.Mcp;
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

    /// <summary>
    /// The exact anonymous discovery paths mapped by <see cref="MapMcpDiscovery"/>. Forwards to
    /// <see cref="McpDiscoveryPaths.AnonymousDiscoveryPaths"/> in Beacon.Core, which is the single
    /// source of truth: the auth middlewares in Beacon.Api allow-list the same list, and Api must
    /// not reference MCP (§2.4). Allow-lists must cover ONLY these exact paths, never a
    /// <c>/.well-known</c> prefix — a prefix match would let <c>/.well-known/anything</c> reach the
    /// SPA fallback anonymously (R6-2).
    /// </summary>
    public static IReadOnlyList<string> AnonymousDiscoveryPaths => McpDiscoveryPaths.AnonymousDiscoveryPaths;

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
        // Mapped from AnonymousDiscoveryPaths so the routes can never drift from the exact paths
        // the host middlewares allow-list.
        endpoints.MapGet(AnonymousDiscoveryPaths[0], HandleProtectedResourceMetadata)
            .AllowAnonymous()
            .ExcludeFromDescription();
        endpoints.MapGet(AnonymousDiscoveryPaths[1], HandleProtectedResourceMetadata)
            .AllowAnonymous()
            .ExcludeFromDescription();
        endpoints.MapGet(AnonymousDiscoveryPaths[2], HandleServerCard)
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
    /// parameter, or null when the response header must stay untouched. The existing value is parsed
    /// into comma-separated challenges (pragmatic RFC 7235 parse — a quote-aware comma split, where a
    /// segment whose first token is not <c>key=value</c> is a scheme token starting a new challenge).
    /// Rules (R6-5): no existing header → a fresh <c>Bearer resource_metadata="…"</c> challenge; a
    /// Bearer challenge already carrying a <c>resource_metadata</c> auth-param → untouched (null); a
    /// Bearer challenge without it (JwtBearerAuthMiddleware writes a bare <c>Bearer</c> on invalid
    /// tokens) → the parameter is appended to THAT challenge, preserving its auth-params; no Bearer
    /// challenge at all → a SEPARATE <c>Bearer resource_metadata="…"</c> challenge is appended rather
    /// than mutating a foreign scheme. Internal for unit tests (InternalsVisibleTo Beacon.Tests).
    /// </summary>
    internal static string? BuildResourceMetadataChallenge(string? existingValue, string metadataUrl)
    {
        var parameter = $"resource_metadata=\"{metadataUrl}\"";
        if (string.IsNullOrWhiteSpace(existingValue))
        {
            return $"Bearer {parameter}";
        }

        var challenges = SplitChallenges(existingValue);
        var bearerIndex = challenges.FindIndex(x => IsBearerChallenge(x));
        if (bearerIndex < 0)
        {
            // resource_metadata is a Bearer auth-param (RFC 9728 §5.1) — never mutate a foreign
            // scheme; advertise Bearer as its own additional challenge instead.
            return $"{existingValue.Trim()}, Bearer {parameter}";
        }

        if (challenges.Any(x => IsBearerChallenge(x) && CarriesResourceMetadata(x)))
        {
            return null;
        }

        // Append the parameter to the first Bearer challenge, preserving its existing auth-params.
        var bearerChallenge = challenges[bearerIndex];
        challenges[bearerIndex] = bearerChallenge.Count == 1 && !bearerChallenge[0].Any(char.IsWhiteSpace)
            ? [$"{bearerChallenge[0]} {parameter}"]
            : [.. bearerChallenge, parameter];

        return string.Join(", ", challenges.Select(x => string.Join(", ", x)));
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

    // Pragmatic RFC 7235 parse: split the header on top-level commas (quoted strings respected),
    // then group segments into challenges — a segment whose first token is not `key=value` is a
    // scheme token starting a new challenge; a `key=value` first token is an auth-param continuing
    // the previous one. Each challenge is its list of comma-separated segments.
    private static List<List<string>> SplitChallenges(string headerValue)
    {
        var challenges = new List<List<string>>();
        foreach (var segment in SplitTopLevelSegments(headerValue))
        {
            if (challenges.Count == 0 || !FirstToken(segment).Contains('='))
            {
                challenges.Add([segment]);
                continue;
            }

            challenges[^1].Add(segment);
        }

        return challenges;
    }

    private static List<string> SplitTopLevelSegments(string headerValue)
    {
        var segments = new List<string>();
        var start = 0;
        var inQuotes = false;
        for (var i = 0; i < headerValue.Length; i++)
        {
            var current = headerValue[i];
            if (inQuotes && current == '\\')
            {
                i++;
                continue;
            }

            if (current == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (current == ',' && !inQuotes)
            {
                AddTrimmedNonEmpty(segments, headerValue[start..i]);
                start = i + 1;
            }
        }

        AddTrimmedNonEmpty(segments, headerValue[start..]);
        return segments;
    }

    private static void AddTrimmedNonEmpty(List<string> segments, string segment)
    {
        var trimmed = segment.Trim();
        if (trimmed.Length > 0)
        {
            segments.Add(trimmed);
        }
    }

    private static string FirstToken(string segment)
    {
        var separatorIndex = segment.IndexOfAny([' ', '\t']);
        return separatorIndex < 0 ? segment : segment[..separatorIndex];
    }

    // The scheme is the challenge's first token — "BearerX" is NOT the Bearer scheme, and scheme
    // comparison is case-insensitive (RFC 7235 §2.1).
    private static bool IsBearerChallenge(List<string> challenge)
    {
        return FirstToken(challenge[0]).Equals("Bearer", StringComparison.OrdinalIgnoreCase);
    }

    // True when the Bearer challenge carries a resource_metadata auth-param — exact param-NAME
    // match, never a substring test (a foreign param value merely containing the text
    // "resource_metadata" must not count as the parameter being present).
    private static bool CarriesResourceMetadata(List<string> challenge)
    {
        var parameters = new List<string>();
        var schemeRemainder = challenge[0][FirstToken(challenge[0]).Length..].Trim();
        if (schemeRemainder.Length > 0)
        {
            parameters.Add(schemeRemainder);
        }

        parameters.AddRange(challenge.Skip(1));
        return parameters
            .Select(x => ParameterName(x))
            .Any(x => string.Equals(x, "resource_metadata", StringComparison.OrdinalIgnoreCase));
    }

    private static string? ParameterName(string parameter)
    {
        var equalsIndex = parameter.IndexOf('=');
        return equalsIndex < 0 ? null : parameter[..equalsIndex].Trim();
    }
}
