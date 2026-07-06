using System.Net.Http.Headers;
using System.Text;
using Beacon.Connector.Api.Models;

namespace Beacon.Connector.Api.Services;

public static class ApiHttpClientHelper
{
    public static HttpRequestMessage CreateRequest(
        ApiConnectionConfig config,
        ApiQueryDefinition query)
    {
        var url = BuildUrl(config.BaseUrl, query.Path, query.Parameters);

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

        // Apply auth
        ApplyAuth(request, config.Auth);

        // Apply custom headers from query parameters
        if (query.Parameters?.Header != null)
        {
            foreach (var header in query.Parameters.Header)
            {
                request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

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

    private static string BuildUrl(string baseUrl, string path, ApiQueryParameters? parameters)
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

        var url = baseUrl.TrimEnd('/') + resolvedPath;

        // Add query parameters
        if (parameters?.Query != null && parameters.Query.Count > 0)
        {
            var queryString = string.Join("&",
                parameters.Query.Select(kvp =>
                    $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
            url += "?" + queryString;
        }

        // Verify the composed URL still targets the configured host — defends against
        // path-based host injection slipping past the prefix check.
        var configuredHost = new Uri(baseUrl).Host;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var builtUri) ||
            !string.Equals(builtUri.Host, configuredHost, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Resolved request URL host does not match the configured base URL host '{configuredHost}'.");
        }

        return url;
    }
}
