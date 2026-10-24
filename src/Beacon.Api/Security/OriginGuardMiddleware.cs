using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Beacon.Api.Security;

/// <summary>Why the origin check refuses a request; <see cref="Allowed"/> when it does not.</summary>
internal enum OriginGuardVerdict
{
    Allowed,

    /// <summary>A browser named a Host that is not one of the configured origins.</summary>
    ForeignHost,

    /// <summary>The <c>Origin</c> header is not an allowed origin (or is the opaque <c>null</c> origin).</summary>
    ForeignOrigin,

    /// <summary>No <c>Origin</c> header, but the browser reports the request as cross-site or same-site.</summary>
    CrossSiteRequest
}

/// <summary>
/// The Host and Origin check on Beacon's routes (<see cref="BeaconRequestPaths"/>). It looks at the requests a page on
/// another origin could use: state-changing methods, the realtime hub (negotiate and the WebSocket upgrade) and anonymous
/// endpoints. Their <c>Origin</c> must be an allowed origin (<see cref="BeaconHostSecurityOptions.AllowedOrigins"/> and
/// <c>Beacon:PublicBaseUrl</c>, or the request's own origin when neither is set); without an <c>Origin</c>, a browser's
/// <c>Sec-Fetch-Site</c> must not be cross-site or same-site. With configured origins, a browser's Host must also be one of
/// them, so Beacon answers browsers only under the names it is configured for (without configured origins the Host check does
/// not run, so DNS-rebinding protection needs them). Requests with an <c>Authorization</c> header pass: they are not
/// cookie-authenticated. In the MCP gateway the check runs before routing, so its anonymous-endpoint rule does not apply there. Requests no browser sent (no <c>Origin</c>, no
/// <c>Sec-Fetch-Site</c>: API keys, MCP clients, probes) pass, and so do an OpenID Connect scheme's own callback paths
/// (the identity provider posts there from its origin), also where the check runs before authentication (the MCP gateway).
/// </summary>
internal sealed class OriginGuardMiddleware
{
    private const string FetchSiteHeader = "Sec-Fetch-Site";
    private const string PublicBaseUrlKey = "Beacon:PublicBaseUrl";

    private readonly RequestDelegate _next;
    private readonly BeaconOriginGuardMode _mode;
    private readonly IReadOnlyList<BeaconOrigin> _configured;
    private readonly ILogger<OriginGuardMiddleware> _logger;

    public OriginGuardMiddleware(
        RequestDelegate next,
        IOptions<BeaconHostSecurityOptions> options,
        IConfiguration configuration,
        ILogger<OriginGuardMiddleware> logger)
    {
        _next = next;
        _logger = logger;
        _mode = options.Value.OriginGuard;
        _configured = ConfiguredOrigins(options.Value, configuration[PublicBaseUrlKey], logger);
        if (_mode == BeaconOriginGuardMode.Enforce && _configured.Count == 0)
        {
            logger.LogWarning(
                "Beacon:Security:OriginGuard is Enforce but no origin is configured (Beacon:Security:AllowedOrigins, Beacon:PublicBaseUrl), so the check compares each Origin with the request's own scheme and Host. Behind a TLS-terminating proxy that does not forward the scheme every browser write is refused, and without configured origins the Host check does not run, so it gives no DNS-rebinding protection.");
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!BeaconRequestPaths.IsBeaconRequest(context))
        {
            await _next(context);
            return;
        }

        var verdict = Evaluate(context, _configured);
        if (verdict == OriginGuardVerdict.Allowed || await IsRemoteSignInPathAsync(context))
        {
            await _next(context);
            return;
        }

        if (_mode == BeaconOriginGuardMode.LogOnly)
        {
            _logger.LogWarning(
                "The Beacon origin check would refuse {Method} {Path} ({Verdict}); the request was let through because Beacon:Security:OriginGuard is LogOnly. List the origins browsers use in Beacon:Security:AllowedOrigins or Beacon:PublicBaseUrl.",
                context.Request.Method,
                context.Request.Path.Value,
                verdict);
            await _next(context);
            return;
        }

        _logger.LogWarning(
            "The Beacon origin check refused {Method} {Path} ({Verdict}).",
            context.Request.Method,
            context.Request.Path.Value,
            verdict);
        await Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden",
                detail: "The request's origin is not allowed.")
            .ExecuteAsync(context);
    }

    /// <summary>
    /// Whether an OpenID Connect scheme answers this path itself (its callback, signed-out callback or remote sign-out):
    /// the identity provider posts there from its own origin. Asked only for a request the check would refuse.
    /// </summary>
    internal static async Task<bool> IsRemoteSignInPathAsync(HttpContext context)
    {
        var schemes = context.RequestServices.GetService<IAuthenticationSchemeProvider>();
        var oidc = context.RequestServices.GetService<IOptionsMonitor<OpenIdConnectOptions>>();
        if (schemes == null || oidc == null)
        {
            return false;
        }

        var path = context.Request.Path;
        foreach (var scheme in await schemes.GetRequestHandlerSchemesAsync())
        {
            if (!typeof(OpenIdConnectHandler).IsAssignableFrom(scheme.HandlerType))
            {
                continue;
            }

            var options = oidc.Get(scheme.Name);
            if (options.CallbackPath == path || options.SignedOutCallbackPath == path || options.RemoteSignOutPath == path)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The check itself, for a request on Beacon's routes; <paramref name="configured"/> empty means the request's own origin.</summary>
    internal static OriginGuardVerdict Evaluate(HttpContext context, IReadOnlyList<BeaconOrigin> configured)
    {
        var request = context.Request;
        if (request.Headers.ContainsKey(HeaderNames.Authorization))
        {
            // A page on another origin cannot add an Authorization header without a CORS preflight, so a request that
            // carries one (API key, bearer token) is not the ambient-cookie request the check is for.
            return OriginGuardVerdict.Allowed;
        }

        var stateChanging = !HttpMethods.IsGet(request.Method)
            && !HttpMethods.IsHead(request.Method)
            && !HttpMethods.IsOptions(request.Method)
            && !HttpMethods.IsTrace(request.Method);
        var hub = request.Path.StartsWithSegments(BeaconApiEndpoints.HubPath, StringComparison.OrdinalIgnoreCase);
        var anonymous = context.GetEndpoint()?.Metadata.GetMetadata<IAllowAnonymous>() != null;
        if (!stateChanging && !hub && !anonymous)
        {
            return OriginGuardVerdict.Allowed;
        }

        var origin = request.Headers.Origin.ToString();
        var fetchSite = request.Headers[FetchSiteHeader].ToString();
        var fromBrowser = origin.Length > 0 || fetchSite.Length > 0;
        if (configured.Count > 0 && fromBrowser && !configured.Any(x => x.MatchesHost(request.Host)))
        {
            return OriginGuardVerdict.ForeignHost;
        }

        if (!stateChanging && !hub)
        {
            return OriginGuardVerdict.Allowed;
        }

        if (origin.Length > 0)
        {
            var parsed = BeaconOrigin.TryParseHeader(origin);
            var allowed = configured.Count > 0
                ? configured.Contains(parsed)
                : parsed == BeaconOrigin.FromRequest(request);

            return parsed != null && allowed
                ? OriginGuardVerdict.Allowed
                : OriginGuardVerdict.ForeignOrigin;
        }

        return string.Equals(fetchSite, "cross-site", StringComparison.OrdinalIgnoreCase)
            || string.Equals(fetchSite, "same-site", StringComparison.OrdinalIgnoreCase)
                ? OriginGuardVerdict.CrossSiteRequest
                : OriginGuardVerdict.Allowed;
    }

    internal static IReadOnlyList<BeaconOrigin> ConfiguredOrigins(BeaconHostSecurityOptions options, string? publicBaseUrl, ILogger logger)
    {
        // The validator refuses an unreadable AllowedOrigins entry at startup; one that still reaches here is skipped.
        var origins = options.AllowedOrigins
            .Select(BeaconOrigin.TryParseConfigured)
            .OfType<BeaconOrigin>()
            .ToList();

        if (!string.IsNullOrWhiteSpace(publicBaseUrl))
        {
            var publicOrigin = BeaconOrigin.TryParseBaseUrl(publicBaseUrl);
            if (publicOrigin == null)
            {
                logger.LogWarning("Beacon:PublicBaseUrl is not an absolute http or https URL, so the Beacon origin check does not count it as an allowed origin.");
            }
            else
            {
                origins.Add(publicOrigin);
            }
        }

        return origins
            .Distinct()
            .ToList();
    }
}
