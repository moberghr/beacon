using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Beacon.Core.Mcp;

namespace Beacon.Testing;

/// <summary>
/// A real MCP client — the official SDK's, over Streamable HTTP — connected to <c>/beacon/mcp</c> of a test server with a
/// bearer token, so a test goes through everything a production call does: the bearer middleware, the caller mapping, the
/// route policy, the MCP filters and audit, the tool dispatch and the endpoint's own authorization.
/// </summary>
public sealed class McpTestClient : IAsyncDisposable
{
    private readonly McpClient _client;

    private McpTestClient(McpClient client) => _client = client;

    /// <summary>
    /// Connects with <paramref name="token"/> (from <see cref="McpTestTokens"/>) through <paramref name="httpClient"/> — the
    /// test server's (<c>app.GetTestClient()</c>, <c>factory.CreateClient()</c>). Throws when the server refuses the
    /// connection (no token, an unmapped caller).
    /// </summary>
    public static async Task<McpTestClient> ConnectAsync(HttpClient httpClient, string token, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress ?? new Uri("http://localhost"), McpDiscoveryPaths.McpPath),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            httpClient,
            NullLoggerFactory.Instance,
            ownsHttpClient: false);

        return new McpTestClient(await McpClient.CreateAsync(transport, cancellationToken: cancellationToken));
    }

    /// <summary>The tools the caller is offered, by name.</summary>
    public async Task<IReadOnlyList<Tool>> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var tools = await _client.ListToolsAsync(cancellationToken: cancellationToken);

        return tools
            .Select(x => x.ProtocolTool)
            .ToList();
    }

    /// <summary>Calls <paramref name="name"/> (<c>api_my_assets</c>, say) with <paramref name="arguments"/>.</summary>
    public Task<CallToolResult> CallToolAsync(string name, IReadOnlyDictionary<string, object?>? arguments = null, CancellationToken cancellationToken = default) =>
        _client.CallToolAsync(name, arguments, cancellationToken: cancellationToken).AsTask();

    /// <summary>The text of a tool result: the JSON an endpoint returned, or the tool error.</summary>
    public static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(x => x.Text));

    /// <summary>The structured JSON of a successful result.</summary>
    public static JsonElement Json(CallToolResult result) =>
        result.StructuredContent ?? throw new InvalidOperationException($"The tool returned no structured content: {Text(result)}");

    public ValueTask DisposeAsync() => _client.DisposeAsync();
}
