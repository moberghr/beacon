using System.Net.Http.Headers;
using System.Text;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Beacon.Core.Data.Enums;
using Beacon.Core.Notifications;

namespace Beacon.Core.Adapters.Jira;

public interface IJiraRestClient
{
    Task<JiraSearchResponse> SearchAsync(string jql, int maxResults = 50, int startAt = 0, string? fields = null, CancellationToken cancellationToken = default);

    Task<JiraCreateIssueResponse> CreateIssueAsync(JiraCreateIssueRequest request, CancellationToken cancellationToken = default);

    Task<JiraCommentResponse> AddCommentAsync(string issueIdOrKey, JiraAddCommentRequest request, CancellationToken cancellationToken = default);

    Task<JiraTransitionsResponse> GetTransitionsAsync(string issueIdOrKey, CancellationToken cancellationToken = default);

    Task TransitionIssueAsync(string issueIdOrKey, JiraTransitionRequest request, CancellationToken cancellationToken = default);
}

public interface IJiraRestClientFactory
{
    IJiraRestClient CreateClient(JiraCredentials credentials);
}

/// <summary>
/// Jira clients on the notification outbound policy (<see cref="NotificationHttpClient"/>, the Jira client): no
/// redirects, connect-time address checks with Jira's own private-network allowances, HTTP/1.1 only, a timeout and a
/// response size cap.
/// </summary>
public class JiraRestClientFactory(IHttpClientFactory httpClientFactory) : IJiraRestClientFactory
{
    public IJiraRestClient CreateClient(JiraCredentials credentials)
    {
        var httpClient = httpClientFactory.CreateClient(NotificationHttpClient.NameFor(NotificationType.Jira));
        httpClient.BaseAddress = new Uri(credentials.DomainUrl);

        var authValue = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{credentials.Email}:{credentials.ApiKey}"));
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authValue);

        return new JiraRestClient(httpClient);
    }
}

internal sealed class JiraRestClient(HttpClient httpClient) : IJiraRestClient
{
    // The options the client has always used on the wire: case-insensitive camelCase names, camelCase string enums,
    // strict numbers, and an object-typed member read as the value it holds (a string stays a string).
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new InferredObjectConverter(), new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    // A Jira served under a context path (https://host/jira) keeps it: a rooted request path would drop it.
    private string BasePath => httpClient.BaseAddress?.AbsolutePath.TrimEnd('/') ?? string.Empty;

    public async Task<JiraSearchResponse> SearchAsync(string jql, int maxResults = 50, int startAt = 0, string? fields = null, CancellationToken cancellationToken = default)
    {
        var url = $"{BasePath}/rest/api/3/search/jql?jql={Uri.EscapeDataString(jql)}&maxResults={maxResults}&startAt={startAt}";
        if (fields != null)
        {
            url += $"&fields={Uri.EscapeDataString(fields)}";
        }

        using var response = await httpClient.GetAsync(url, cancellationToken);
        return await ReadAsync<JiraSearchResponse>(response, cancellationToken);
    }

    public async Task<JiraCreateIssueResponse> CreateIssueAsync(JiraCreateIssueRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync($"{BasePath}/rest/api/3/issue", request, JsonOptions, cancellationToken);
        return await ReadAsync<JiraCreateIssueResponse>(response, cancellationToken);
    }

    public async Task<JiraCommentResponse> AddCommentAsync(string issueIdOrKey, JiraAddCommentRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync($"{BasePath}/rest/api/3/issue/{Uri.EscapeDataString(issueIdOrKey)}/comment", request, JsonOptions, cancellationToken);
        return await ReadAsync<JiraCommentResponse>(response, cancellationToken);
    }

    public async Task<JiraTransitionsResponse> GetTransitionsAsync(string issueIdOrKey, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync($"{BasePath}/rest/api/3/issue/{Uri.EscapeDataString(issueIdOrKey)}/transitions", cancellationToken);
        return await ReadAsync<JiraTransitionsResponse>(response, cancellationToken);
    }

    public async Task TransitionIssueAsync(string issueIdOrKey, JiraTransitionRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync($"{BasePath}/rest/api/3/issue/{Uri.EscapeDataString(issueIdOrKey)}/transitions", request, JsonOptions, cancellationToken);
        EnsureSuccess(response);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        EnsureSuccess(response);

        // Read whatever the Content-Type says; a body that is empty, null, HTML or not the expected JSON is an
        // unreadable answer that carries the status, never the remote text.
        T? result;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            result = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            throw new HttpRequestException("Jira returned a response that could not be read.", null, response.StatusCode);
        }

        return result ?? throw new HttpRequestException("Jira returned a response that could not be read.", null, response.StatusCode);
    }

    // The response body is remote content: it is never read into the exception (the status alone is carried).
    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Jira returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }
    }
}

internal sealed class InferredObjectConverter : JsonConverter<object>
{
    public override object? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Number when reader.TryGetInt64(out var number) => number,
            JsonTokenType.Number => reader.GetDouble(),
            JsonTokenType.String when reader.TryGetDateTime(out var dateTime) => dateTime,
            JsonTokenType.String => reader.GetString(),
            _ => JsonDocument.ParseValue(ref reader).RootElement.Clone(),
        };
    }

    public override void Write(Utf8JsonWriter writer, object value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
