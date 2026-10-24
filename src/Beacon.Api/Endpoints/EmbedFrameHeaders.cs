using Beacon.Core.Services.Embed;

namespace Beacon.Api.Endpoints;

/// <summary>
/// The response headers of Beacon's embed page: a strict Content-Security-Policy whose <c>frame-ancestors</c> lists the
/// grant's origins (<c>'none'</c> for a grant that is not live, or none listed), no referrer, no caching, no sniffing.
/// </summary>
internal static class EmbedFrameHeaders
{
    public const string BasePolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; connect-src 'self'; img-src 'self' data:; font-src 'self'; base-uri 'none'; form-action 'none'";

    /// <summary>The page's Content-Security-Policy for <paramref name="origins"/>.</summary>
    public static string ContentSecurityPolicy(IReadOnlyList<string> origins)
    {
        // Each origin again as the grant's rules write one, so nothing stored can add a directive to the header.
        var allowed = origins
            .Where(IsOrigin)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var ancestors = allowed.Count == 0 ? "'none'" : string.Join(' ', allowed);

        return $"{BasePolicy}; frame-ancestors {ancestors}";
    }

    public static void Apply(HttpResponse response, IReadOnlyList<string> origins)
    {
        response.Headers.ContentSecurityPolicy = ContentSecurityPolicy(origins);
        response.Headers["Referrer-Policy"] = "no-referrer";
        response.Headers.CacheControl = "no-store";
        response.Headers.XContentTypeOptions = "nosniff";
    }

    private static bool IsOrigin(string origin)
    {
        try
        {
            return EmbedOriginRules.Normalize([origin], 1, allowLocalhost: true)[0] == origin;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
