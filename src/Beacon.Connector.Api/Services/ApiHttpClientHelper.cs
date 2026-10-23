using System.Net.Http.Headers;
using System.Text;
using Beacon.Connector.Api.Models;
using Beacon.Core.Models;

namespace Beacon.Connector.Api.Services;

public static class ApiHttpClientHelper
{
    // Headers a query may not set: routing (Host, forwarding, URL rewrites), method overrides, framing and connection
    // management, and credentials.
    private static readonly HashSet<string> RestrictedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host",
        "Forwarded",
        "Via",
        "X-Real-IP",
        "X-Client-IP",
        "True-Client-IP",
        "CF-Connecting-IP",
        "X-Cluster-Client-IP",
        "Fastly-Client-IP",
        "X-Original-URL",
        "X-Rewrite-URL",
        "X-Original-Host",
        "X-Host",
        "X-HTTP-Method",
        "X-HTTP-Method-Override",
        "X-Method-Override",
        "Content-Length",
        "Transfer-Encoding",
        "TE",
        "Trailer",
        "Connection",
        "Keep-Alive",
        "Upgrade",
        "Expect",
        "Authorization",
        "Cookie"
    };

    private static readonly string[] RestrictedHeaderPrefixes = ["X-Forwarded-", "Proxy-"];

    public static HttpRequestMessage CreateRequest(
        ApiConnectionConfig config,
        ApiQueryDefinition query)
    {
        var url = BuildUrl(config.BaseUrl, query.Path, query.Parameters, config.Auth);

        // Enforce a read-only verb whitelist. Only GET/HEAD/OPTIONS are allowed by
        // default; POST is permitted solely when the connection explicitly opts in
        // for search-style endpoints. Mutating verbs (PUT/DELETE/PATCH) and any other
        // arbitrary method are rejected — never forwarded verbatim.
        var method = query.Method.ToUpperInvariant() switch
        {
            "GET" => HttpMethod.Get,
            "HEAD" => HttpMethod.Head,
            "OPTIONS" => HttpMethod.Options,
            "POST" when config.AllowPostQueries => HttpMethod.Post,
            "POST" => throw new InvalidOperationException(
                "POST queries are disabled for this API data source. Enable AllowPostQueries for search-style endpoints."),
            _ => throw new InvalidOperationException(
                $"HTTP method '{query.Method}' is not permitted. Only read-only verbs (GET/HEAD/OPTIONS) are allowed.")
        };

        var request = new HttpRequestMessage(method, url);

        // The query's own headers first, each checked: none may route the request elsewhere, change its method or framing,
        // or replace the data source's credentials, which are applied after them.
        if (query.Parameters?.Header != null)
        {
            foreach (var header in query.Parameters.Header)
            {
                EnsureHeaderAllowed(header.Key, header.Value, config.Auth);
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        ApplyAuth(request, config.Auth);

        // Set body if present
        if (!string.IsNullOrEmpty(query.Body))
        {
            request.Content = new StringContent(query.Body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    public static void ApplyAuth(HttpRequestMessage request, ApiAuthConfig? auth)
    {
        if (auth == null || auth.Type == ApiAuthType.None)
            return;

        switch (auth.Type)
        {
            case ApiAuthType.Bearer:
                if (!string.IsNullOrEmpty(auth.Token))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", auth.Token);
                break;

            case ApiAuthType.Basic:
                if (!string.IsNullOrEmpty(auth.Username))
                {
                    var credentials = Convert.ToBase64String(
                        Encoding.UTF8.GetBytes($"{auth.Username}:{auth.Password ?? ""}"));
                    request.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
                }
                break;

            case ApiAuthType.ApiKey:
                if (!string.IsNullOrEmpty(auth.ApiKeyName) && !string.IsNullOrEmpty(auth.ApiKeyValue))
                {
                    if (auth.ApiKeyLocation == ApiKeyLocation.Query)
                    {
                        var uri = request.RequestUri!.ToString();
                        var separator = uri.Contains('?') ? "&" : "?";
                        request.RequestUri = new Uri(
                            $"{uri}{separator}{Uri.EscapeDataString(auth.ApiKeyName)}={Uri.EscapeDataString(auth.ApiKeyValue)}");
                    }
                    else
                    {
                        request.Headers.TryAddWithoutValidation(auth.ApiKeyName, auth.ApiKeyValue);
                    }
                }
                break;
        }
    }

    /// <summary>
    /// Reads the body of a response that was fetched with <see cref="HttpCompletionOption.ResponseHeadersRead"/>, refusing
    /// one larger than <paramref name="maxBytes"/> (by its declared length, or as soon as more arrives) with a message that
    /// names <paramref name="what"/> and the limit only.
    /// </summary>
    internal static async Task<MemoryStream> ReadCappedAsync(HttpResponseMessage response, long maxBytes, string what, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw TooLarge(what, maxBytes);
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var body = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (body.Length + read > maxBytes)
            {
                throw TooLarge(what, maxBytes);
            }

            body.Write(chunk, 0, read);
        }

        body.Position = 0;
        return body;
    }

    /// <summary>The encoding the response's Content-Type names, or UTF-8 when it names none or one .NET does not know.</summary>
    internal static Encoding EncodingOf(HttpContent content)
    {
        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    // Same scheme, host and port; anything that cannot be read is not the same origin.
    internal static bool SameOrigin(string? url, string? baseUrl) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && Uri.TryCreate(baseUrl, UriKind.Absolute, out var baseUri)
        && Uri.Compare(uri, baseUri, UriComponents.SchemeAndServer, UriFormat.UriEscaped, StringComparison.OrdinalIgnoreCase) == 0;

    // Redirects are not followed (the client's handler), so a redirect answer is a failure that says so.
    internal static void EnsureNotRedirected(HttpResponseMessage response)
    {
        var status = (int)response.StatusCode;
        if (status is >= 300 and < 400)
        {
            throw new BeaconException(
                $"The API answered with a redirect (HTTP {status}), which Beacon does not follow. Point BaseUrl and OpenApiSpecUrl at the final address.");
        }
    }

    private static BeaconException TooLarge(string what, long maxBytes) =>
        new($"The {what} is larger than the {maxBytes / (1024 * 1024)} MB Beacon reads.");

    private static string BuildUrl(string baseUrl, string path, ApiQueryParameters? parameters, ApiAuthConfig? auth)
    {
        // Substitute path parameters
        var resolvedPath = path;
        if (parameters?.Path != null)
        {
            foreach (var param in parameters.Path)
            {
                resolvedPath = resolvedPath.Replace($"{{{param.Key}}}", Uri.EscapeDataString(param.Value));
            }
        }

        // Guard against host injection (e.g. an absolute URL or `@evil.com/x` smuggled
        // through the path). The resolved path must be server-relative.
        if (!resolvedPath.StartsWith('/'))
        {
            throw new InvalidOperationException(
                $"Endpoint path must be a server-relative path starting with '/'. Got: '{resolvedPath}'.");
        }

        if (resolvedPath.Contains('?'))
        {
            throw new InvalidOperationException("Endpoint path must not contain '?'; pass query values as query parameters.");
        }

        EnsurePathStaysPut(resolvedPath);

        var url = baseUrl.TrimEnd('/') + resolvedPath;

        // Add query parameters
        // The data source's own API key wins: a caller's query value of the same name is dropped, never sent before it.
        var keyName = auth is { Type: ApiAuthType.ApiKey, ApiKeyLocation: ApiKeyLocation.Query } ? auth.ApiKeyName : null;
        var callerQuery = (parameters?.Query ?? [])
            .Where(x => keyName == null || !string.Equals(x.Key, keyName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (callerQuery.Count > 0)
        {
            var queryString = string.Join("&",
                callerQuery.Select(x =>
                    $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
            url += "?" + queryString;
        }

        EnsureUnderBaseUrl(baseUrl, url);

        return url;
    }

    // The path names a resource under the base URL: no dot segment (also percent-encoded, once or more, or behind a
    // backslash), no control character, no fragment.
    private static void EnsurePathStaysPut(string resolvedPath)
    {
        var pathPart = resolvedPath.Split('?', 2)[0];
        if (resolvedPath.Any(char.IsControl) || resolvedPath.Contains('#'))
        {
            throw new InvalidOperationException("Endpoint path must not contain control characters or a fragment ('#').");
        }

        var decoded = pathPart;
        for (var i = 0; i < 5; i++)
        {
            var next = Uri.UnescapeDataString(decoded);
            if (next == decoded)
            {
                break;
            }

            decoded = next;
        }

        var segments = decoded
            .Replace('\\', '/')
            .Split('/');
        // A path parameter (`..;x=1`) is stripped by many servers before they resolve the dot segment.
        if (segments.Select(x => x.Split(';')[0]).Any(x => x is "." or "..") || decoded.Any(char.IsControl))
        {
            throw new InvalidOperationException("Endpoint path must not contain '.' or '..' segments or encoded control characters.");
        }
    }

    // The composed URL keeps the base URL's scheme, host and port and stays under its path, after the URL is normalised.
    private static void EnsureUnderBaseUrl(string baseUrl, string url)
    {
        var baseUri = new Uri(baseUrl);
        if (!Uri.TryCreate(url, UriKind.Absolute, out var builtUri)
            || !string.Equals(builtUri.Scheme, baseUri.Scheme, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(builtUri.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase)
            || builtUri.Port != baseUri.Port
            || !string.IsNullOrEmpty(builtUri.UserInfo))
        {
            throw new InvalidOperationException(
                $"Resolved request URL host does not match the configured base URL host '{baseUri.Host}'.");
        }

        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        var builtPath = builtUri.AbsolutePath;
        if (basePath.Length > 0
            && !string.Equals(builtPath, basePath, StringComparison.Ordinal)
            && !builtPath.StartsWith(basePath + "/", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Resolved request URL is not under the configured base URL's path.");
        }
    }

    // A caller's header: a valid name and value (no CR, LF or other control character), and not one that routes the
    // request, overrides its method, changes its framing or carries credentials.
    private static void EnsureHeaderAllowed(string name, string? value, ApiAuthConfig? auth)
    {
        if (string.IsNullOrEmpty(name) || !name.All(IsTokenChar))
        {
            throw new InvalidOperationException("A header name must be a non-empty HTTP token (letters, digits and !#$%&'*+-.^_`|~).");
        }

        if (value != null && value.Any(x => char.IsControl(x) && x != '\t'))
        {
            throw new InvalidOperationException($"The value of header '{name}' must not contain control characters (CR, LF and the like).");
        }

        var restricted = RestrictedHeaders.Contains(name)
            || RestrictedHeaderPrefixes.Any(x => name.StartsWith(x, StringComparison.OrdinalIgnoreCase))
            || (auth is { Type: ApiAuthType.ApiKey } && string.Equals(name, auth.ApiKeyName, StringComparison.OrdinalIgnoreCase));
        if (restricted)
        {
            throw new InvalidOperationException($"Header '{name}' cannot be set by a query: it routes the request, changes its method or framing, or carries credentials.");
        }
    }

    private static bool IsTokenChar(char x)
    {
        return x is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9')
            or '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or '`' or '|' or '~';
    }
}
