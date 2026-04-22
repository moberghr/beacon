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

        var method = query.Method.ToUpperInvariant() switch
        {
            "GET" => HttpMethod.Get,
            "POST" => HttpMethod.Post,
            "PUT" => HttpMethod.Put,
            "DELETE" => HttpMethod.Delete,
            "PATCH" => HttpMethod.Patch,
            "HEAD" => HttpMethod.Head,
            "OPTIONS" => new HttpMethod("OPTIONS"),
            _ => new HttpMethod(query.Method)
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

        var url = baseUrl.TrimEnd('/') + resolvedPath;

        // Add query parameters
        if (parameters?.Query != null && parameters.Query.Count > 0)
        {
            var queryString = string.Join("&",
                parameters.Query.Select(kvp =>
                    $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value)}"));
            url += "?" + queryString;
        }

        return url;
    }
}
