using Beacon.Core.Services.Embed;
using Microsoft.Extensions.Options;

namespace Beacon.Api.Authentication;

/// <summary>Which of the anonymous embed routes a request is for.</summary>
public enum EmbedRoute
{
    None,

    /// <summary><c>POST /beacon/api/embed/tokens</c>: a host's backend exchanges a grant secret for a token.</summary>
    Tokens,

    /// <summary><c>POST /beacon/api/embed/render</c>: Beacon's embed page renders the grant's query with its token.</summary>
    Render,

    /// <summary><c>GET /beacon/embed/{publicId}</c>: the embed page itself.</summary>
    Frame
}

/// <summary>
/// The anonymous embed routes, matched the way routing matches them: by whole segments, ignoring case, with or without a
/// trailing slash, against <see cref="HttpRequest.Path"/> (without the host's path base), whatever the method. While
/// embedding is on (<c>Beacon:Embed:Enabled</c>, never in gateway mode) these paths are not Beacon requests
/// (<see cref="BeaconRequestPaths.IsBeaconRequest"/>): the API-key, bearer, cookie, login-form and first-run middlewares
/// and the cookie's session events leave them alone, and the embed middleware alone decides who the caller is.
/// </summary>
public static class BeaconEmbedPaths
{
    public const string TokensPath = "/beacon/api/embed/tokens";
    public const string RenderPath = "/beacon/api/embed/render";
    public const string FramePrefix = "/beacon/embed";

    /// <summary>Embedding is on for this host: enabled, and not an MCP gateway (which serves no embed route).</summary>
    public static bool IsEnabled(IServiceProvider? services)
    {
        return services?.GetService<IOptions<EmbedOptions>>()?.Value.Enabled == true
            && services.GetService<BeaconApiOptions>()?.GatewayMode != true;
    }

    public static bool IsEmbedRequest(HttpContext context)
    {
        return RouteOf(context) != EmbedRoute.None;
    }

    /// <summary>The embed route the request is for, or <see cref="EmbedRoute.None"/> (always while embedding is off).</summary>
    public static EmbedRoute RouteOf(HttpContext context)
    {
        if (!IsEnabled(context.RequestServices))
        {
            return EmbedRoute.None;
        }

        return RouteOf(context.Request.Path);
    }

    /// <summary>The embed route <paramref name="path"/> names, whether or not embedding is on.</summary>
    public static EmbedRoute RouteOf(PathString path)
    {
        var segments = Segments(path);
        if (segments == null)
        {
            return EmbedRoute.None;
        }

        if (segments.Count == 4
            && Is(segments[0], "beacon")
            && Is(segments[1], "api")
            && Is(segments[2], "embed"))
        {
            if (Is(segments[3], "tokens"))
            {
                return EmbedRoute.Tokens;
            }

            return Is(segments[3], "render") ? EmbedRoute.Render : EmbedRoute.None;
        }

        return segments.Count == 3 && Is(segments[0], "beacon") && Is(segments[1], "embed")
            ? EmbedRoute.Frame
            : EmbedRoute.None;
    }

    // The path's segments, a single trailing slash allowed; null for an empty segment anywhere else ("//", a leading "/" only).
    private static List<string>? Segments(PathString path)
    {
        var value = path.Value;
        if (string.IsNullOrEmpty(value) || value[0] != '/')
        {
            return null;
        }

        var trimmed = value.EndsWith('/') ? value[1..^1] : value[1..];
        var segments = trimmed.Split('/');

        return segments.Any(x => x.Length == 0) ? null : segments.ToList();
    }

    private static bool Is(string segment, string expected)
    {
        return string.Equals(segment, expected, StringComparison.OrdinalIgnoreCase);
    }
}
