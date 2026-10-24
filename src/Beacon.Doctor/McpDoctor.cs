using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Beacon.Doctor;

internal enum CheckStatus
{
    Pass,
    Warn,
    Fail
}

internal sealed record CheckResult(CheckStatus Status, string Check, string Detail);

/// <summary>
/// Checks an MCP endpoint the way an OAuth-capable MCP client finds its way to it: the unauthenticated 401 and its
/// <c>resource_metadata</c> pointer, the protected-resource document (its <c>resource</c>, authorization servers and
/// scopes), the authorization server's own metadata (issuer, signing keys, PKCE), the documents a client probes that must
/// be absent, and with a token the MCP handshake and the tool list. Never sends the token anywhere but the endpoint.
/// </summary>
internal sealed partial class McpDoctor(HttpClient http)
{
    private const string Offline = "offline_access";

    public async Task<IReadOnlyList<CheckResult>> RunAsync(Uri endpoint, string? token, CancellationToken cancellationToken)
    {
        var results = new List<CheckResult>();
        var metadataUrl = await CheckChallengeAsync(endpoint, results, cancellationToken);
        var authorizationServer = metadataUrl == null ? null : await CheckProtectedResourceAsync(endpoint, metadataUrl, results, cancellationToken);
        if (authorizationServer != null)
        {
            await CheckAuthorizationServerAsync(authorizationServer, results, cancellationToken);
        }

        await CheckAbsentDocumentAsync(new Uri(endpoint, "/.well-known/oauth-authorization-server"), results, cancellationToken);

        if (token != null)
        {
            await CheckHandshakeAsync(endpoint, token, results, cancellationToken);
        }
        else
        {
            results.Add(new CheckResult(CheckStatus.Warn, "handshake", "No --token given: initialize and tools/list were not tried."));
        }

        return results;
    }

    private async Task<Uri?> CheckChallengeAsync(Uri endpoint, List<CheckResult> results, CancellationToken cancellationToken)
    {
        using var response = await http.SendAsync(Initialize(endpoint, token: null), cancellationToken);
        if (response.StatusCode != HttpStatusCode.Unauthorized)
        {
            results.Add(new CheckResult(CheckStatus.Fail, "challenge", $"Without a token the endpoint answered {(int)response.StatusCode}, not 401: a client never starts sign-in. A redirect means the host's sign-in answers the MCP route."));
            return null;
        }

        var challenge = string.Join(", ", response.Headers.WwwAuthenticate.Select(x => x.ToString()));
        var match = ResourceMetadata().Match(challenge);
        if (!match.Success || !Uri.TryCreate(match.Groups[1].Value, UriKind.Absolute, out var metadataUrl))
        {
            results.Add(new CheckResult(CheckStatus.Fail, "challenge", "The 401 carries no resource_metadata pointer (RFC 9728): a client cannot find the authorization server."));
            return null;
        }

        results.Add(new CheckResult(CheckStatus.Pass, "challenge", $"401 with resource_metadata={metadataUrl}"));

        return metadataUrl;
    }

    private async Task<string?> CheckProtectedResourceAsync(Uri endpoint, Uri metadataUrl, List<CheckResult> results, CancellationToken cancellationToken)
    {
        var document = await GetJsonAsync(metadataUrl, cancellationToken);
        if (document == null)
        {
            results.Add(new CheckResult(CheckStatus.Fail, "protected-resource", $"{metadataUrl} did not answer a JSON document."));
            return null;
        }

        var resource = document["resource"]?.GetValue<string>();
        var expected = endpoint.GetLeftPart(UriPartial.Path).TrimEnd('/');
        results.Add(string.Equals(resource?.TrimEnd('/'), expected, StringComparison.Ordinal)
            ? new CheckResult(CheckStatus.Pass, "resource", $"resource = {resource}. With Entra it must also be one of the registration's identifierUris, character for character (else AADSTS9010010).")
            : new CheckResult(CheckStatus.Fail, "resource", $"resource is {resource ?? "missing"}, not {expected}: set Beacon:PublicBaseUrl to the URL clients use."));

        var scopes = Strings(document["scopes_supported"]);
        if (scopes.Count == 0)
        {
            results.Add(new CheckResult(CheckStatus.Warn, "scopes", "No scopes_supported: the client will not know which scope to request."));
        }
        else if (scopes.Any(x => x.StartsWith("api://", StringComparison.OrdinalIgnoreCase)) && !scopes.Contains(Offline))
        {
            results.Add(new CheckResult(CheckStatus.Warn, "scopes", $"scopes_supported ({string.Join(", ", scopes)}) lacks offline_access: clients get no refresh token and re-prompt sign-in every hour."));
        }
        else
        {
            results.Add(new CheckResult(CheckStatus.Pass, "scopes", string.Join(", ", scopes)));
        }

        var servers = Strings(document["authorization_servers"]);
        if (servers.Count == 0)
        {
            results.Add(new CheckResult(CheckStatus.Fail, "authorization-servers", "No authorization_servers: a client cannot sign anybody in."));
            return null;
        }

        results.Add(new CheckResult(CheckStatus.Pass, "authorization-servers", servers[0]));

        return servers[0];
    }

    private async Task CheckAuthorizationServerAsync(string issuer, List<CheckResult> results, CancellationToken cancellationToken)
    {
        var metadata = await GetJsonAsync(new Uri(issuer.TrimEnd('/') + "/.well-known/openid-configuration"), cancellationToken)
            ?? await GetJsonAsync(OAuthMetadataUrl(issuer), cancellationToken);
        if (metadata == null)
        {
            results.Add(new CheckResult(CheckStatus.Fail, "authorization-server", $"No OpenID or OAuth metadata found for {issuer}."));
            return;
        }

        var advertised = metadata["issuer"]?.GetValue<string>();
        results.Add(string.Equals(advertised, issuer, StringComparison.Ordinal)
            ? new CheckResult(CheckStatus.Pass, "issuer", advertised!)
            : new CheckResult(CheckStatus.Fail, "issuer", $"The server's metadata names issuer {advertised ?? "none"}, not {issuer}: clients reject the mismatch."));

        if (metadata["jwks_uri"]?.GetValue<string>() is { } jwks && Uri.TryCreate(jwks, UriKind.Absolute, out var jwksUrl))
        {
            using var keys = await http.GetAsync(jwksUrl, cancellationToken);
            results.Add(keys.IsSuccessStatusCode
                ? new CheckResult(CheckStatus.Pass, "signing-keys", jwks)
                : new CheckResult(CheckStatus.Fail, "signing-keys", $"{jwks} answered {(int)keys.StatusCode}."));
        }

        var pkce = Strings(metadata["code_challenge_methods_supported"]);
        results.Add(pkce.Contains("S256")
            ? new CheckResult(CheckStatus.Pass, "pkce", "S256 advertised")
            : new CheckResult(CheckStatus.Warn, "pkce", "The authorization server does not advertise S256 (Microsoft Entra supports it without advertising it). A client that insists on the advertisement refuses to connect; confirm with a real connection."));
    }

    private async Task CheckAbsentDocumentAsync(Uri url, List<CheckResult> results, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken);
        var isHtml = response.Content.Headers.ContentType?.MediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true;
        results.Add(response.IsSuccessStatusCode && isHtml
            ? new CheckResult(CheckStatus.Fail, "absent-documents", $"{url.AbsolutePath} answers {(int)response.StatusCode} with an HTML page: a client reads it as a broken document instead of an absent one. It must be 404.")
            : new CheckResult(CheckStatus.Pass, "absent-documents", $"{url.AbsolutePath} → {(int)response.StatusCode}"));
    }

    private async Task CheckHandshakeAsync(Uri endpoint, string token, List<CheckResult> results, CancellationToken cancellationToken)
    {
        using var initialize = await http.SendAsync(Initialize(endpoint, token), cancellationToken);
        if (!initialize.IsSuccessStatusCode)
        {
            var reason = initialize.StatusCode == HttpStatusCode.Forbidden
                ? "the token is valid but not a caller (scope, client, an unknown person, or the host project not synced — the server log has the reason)"
                : "the token was not accepted (issuer, audience, signature or lifetime)";
            results.Add(new CheckResult(CheckStatus.Fail, "initialize", $"{(int)initialize.StatusCode}: {reason}."));
            return;
        }

        results.Add(new CheckResult(CheckStatus.Pass, "initialize", "the token is a caller"));
        var session = initialize.Headers.TryGetValues("Mcp-Session-Id", out var ids) ? ids.FirstOrDefault() : null;

        using var initialized = await http.SendAsync(Rpc(endpoint, token, session, """{"jsonrpc":"2.0","method":"notifications/initialized"}"""), cancellationToken);
        using var list = await http.SendAsync(Rpc(endpoint, token, session, """{"jsonrpc":"2.0","id":2,"method":"tools/list"}"""), cancellationToken);
        var body = await list.Content.ReadAsStringAsync(cancellationToken);
        var names = ToolNames().Matches(body).Select(x => x.Groups[1].Value).Distinct().ToList();
        results.Add(list.IsSuccessStatusCode && names.Count > 0
            ? new CheckResult(CheckStatus.Pass, "tools", $"{names.Count}: {string.Join(", ", names)}")
            : new CheckResult(CheckStatus.Fail, "tools", $"tools/list answered {(int)list.StatusCode} with no tools."));
    }

    private static HttpRequestMessage Initialize(Uri endpoint, string? token) =>
        Rpc(endpoint, token, session: null, """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"beacon-doctor","version":"1"}}}""");

    private static HttpRequestMessage Rpc(Uri endpoint, string? token, string? session, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (token != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (session != null)
        {
            request.Headers.Add("Mcp-Session-Id", session);
            request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
        }

        return request;
    }

    private async Task<JsonNode?> GetJsonAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(url, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // RFC 8414: the well-known segment goes between the host and the issuer's path.
    private static Uri OAuthMetadataUrl(string issuer)
    {
        var uri = new Uri(issuer);

        return new Uri(uri, "/.well-known/oauth-authorization-server" + uri.AbsolutePath.TrimEnd('/'));
    }

    private static List<string> Strings(JsonNode? node) =>
        node is JsonArray array
            ? array.Select(x => x?.GetValue<string>()).OfType<string>().ToList()
            : [];

    [GeneratedRegex("resource_metadata=\"([^\"]+)\"", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ResourceMetadata();

    [GeneratedRegex("\"name\"\\s*:\\s*\"([^\"]+)\"", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ToolNames();
}
