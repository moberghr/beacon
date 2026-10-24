namespace Beacon.Api.Security;

/// <summary>A web origin as the origin check compares it: lower-case http or https scheme, lower-case host and a port.</summary>
internal sealed record BeaconOrigin(string Scheme, string Host, int Port)
{
    /// <summary>An <c>AllowedOrigins</c> entry: an absolute http(s) URL with no path (a trailing '/' is ignored), query, fragment or user info.</summary>
    public static BeaconOrigin? TryParseConfigured(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim().TrimEnd('/');

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/"
            ? FromUri(uri)
            : null;
    }

    /// <summary>The origin of <c>Beacon:PublicBaseUrl</c>, which may carry a path; the path is not part of the origin.</summary>
    public static BeaconOrigin? TryParseBaseUrl(string? value)
    {
        return Uri.TryCreate((value ?? string.Empty).Trim(), UriKind.Absolute, out var uri)
            ? FromUri(uri)
            : null;
    }

    /// <summary>A request's <c>Origin</c> header; the opaque <c>null</c> origin and anything unreadable give null.</summary>
    public static BeaconOrigin? TryParseHeader(string? value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.AbsolutePath == "/" && !value!.EndsWith('/')
            ? FromUri(uri)
            : null;
    }

    /// <summary>The origin the request itself was made to: its scheme and Host, after any forwarded headers the host applies.</summary>
    public static BeaconOrigin? FromRequest(HttpRequest request)
    {
        if (!request.Host.HasValue)
        {
            return null;
        }

        var scheme = request.Scheme.ToLowerInvariant();

        return new BeaconOrigin(scheme, Punycode(request.Host.Host).ToLowerInvariant(), request.Host.Port ?? DefaultPort(scheme));
    }

    /// <summary>
    /// Whether a request's Host names this origin: the same host, and the same port when the Host carries one (a Host
    /// without a port matches an origin on its scheme's default port, as a TLS-terminating proxy forwards it).
    /// </summary>
    public bool MatchesHost(HostString host)
    {
        if (!host.HasValue || !string.Equals(Punycode(host.Host), Host, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return host.Port == null
            ? Port == DefaultPort(Scheme)
            : host.Port == Port;
    }

    private static BeaconOrigin? FromUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return null;
        }

        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.Host.Length == 0)
        {
            return null;
        }

        return new BeaconOrigin(uri.Scheme, uri.IdnHost.ToLowerInvariant(), uri.Port);
    }

    private static string Punycode(string host)
    {
        if (host.All(char.IsAscii))
        {
            return host;
        }

        return Uri.TryCreate("http://" + host, UriKind.Absolute, out var uri)
            ? uri.IdnHost
            : host;
    }

    private static int DefaultPort(string scheme)
    {
        return scheme == Uri.UriSchemeHttps ? 443 : 80;
    }
}
