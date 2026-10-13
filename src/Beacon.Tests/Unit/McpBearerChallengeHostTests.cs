using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Moq;
using NUnit.Framework;
using Beacon.Api.Authentication;
using Beacon.Core.Authentication;
using Beacon.Core.Mcp;
using Beacon.MCP.Discovery;

namespace Beacon.Tests.Unit;

/// <summary>
/// The MCP OAuth surface as a remote connector (claude.ai, Claude Desktop) sees it, through a real pipeline:
/// <c>UseMcpResourceMetadataChallenge</c> → <c>JwtBearerAuthMiddleware</c> → a stand-in for the Execute-scope policy on
/// <c>/beacon/mcp</c>, plus the anonymous discovery documents. A malformed bearer value must be a 401 carrying the
/// RFC 9728 challenge — never a 500 — and the challenge, the protected-resource metadata and the server card must agree
/// on the resource, the metadata URL and the scopes. No database (§4.7).
/// </summary>
[TestFixture]
public class McpBearerChallengeHostTests
{
    private const string PublicBaseUrl = "https://host.example.com";
    private const string Issuer = "https://login.microsoftonline.com/tenant-1/v2.0";
    private const string V1Issuer = "https://sts.windows.net/tenant-1/";
    private const string Audience = "11111111-2222-3333-4444-555555555555";
    private const string EntraScope = "api://11111111-2222-3333-4444-555555555555/mcp.access";

    // Generated per run: a test-only HMAC key, never a real secret (§1.2).
    private static readonly string SigningKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));

    private static string B64(string json) => Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(json));

    private static IEnumerable<TestCaseData> MalformedTokens()
    {
        var payload = B64($$"""{"sub":"s","iss":"{{Issuer}}","aud":"{{Audience}}","exp":4102444800}""");

        yield return new TestCaseData("not-a-jwt").SetName("Malformed_NotAJwt");
        yield return new TestCaseData("aaa.bbb.ccc").SetName("Malformed_ThreeGarbageSegments");
        yield return new TestCaseData("!!!.@@@.###").SetName("Malformed_NonBase64Segments");
        yield return new TestCaseData($"{B64("hello")}.{B64("world")}.c2ln").SetName("Malformed_SegmentsAreNotJson");
        yield return new TestCaseData("a.b.c.d.e").SetName("Malformed_FiveSegmentGarbage");
        yield return new TestCaseData($"{B64("""{"alg":"none","typ":"JWT"}""")}.{payload}.").SetName("WrongAlg_None");
        yield return new TestCaseData($"{B64("""{"alg":"HS999","typ":"JWT"}""")}.{payload}.c2ln").SetName("WrongAlg_Unknown");
        yield return new TestCaseData($"{B64("""{"alg":"RS256","typ":"JWT","kid":"nope"}""")}.{payload}.c2ln").SetName("WrongAlg_RsaAgainstHmacKey");
        yield return new TestCaseData($"{B64("""{"typ":"JWT"}""")}.{payload}.c2ln").SetName("WrongAlg_Missing");
        yield return new TestCaseData(new string('a', 300_000)).SetName("Malformed_OverTheSizeLimit");
    }

    [TestCaseSource(nameof(MalformedTokens))]
    public async Task MalformedBearer_OnMcp_Is401WithTheResourceMetadataChallenge(string token)
    {
        await using var host = await TestHost.StartAsync();

        var response = await host.PostMcpAsync($"Bearer {token}");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a malformed bearer value is the caller's fault, never a 500");
        var challenge = response.Headers.WwwAuthenticate.ToString();
        challenge.Should().StartWith("Bearer ")
            .And.Contain("error=\"invalid_token\"")
            .And.Contain($"resource_metadata=\"{PublicBaseUrl}/.well-known/oauth-protected-resource\"");
        host.Logs.Should().NotContain(x => x.Contains(token), "the bearer value never reaches a log");
        host.Logs.Should().NotContain(x => x.StartsWith("Error") || x.StartsWith("Critical"), "a bad token is not a server error");
    }

    [TestCase("Bearer ")]
    [TestCase("Bearer     ")]
    public async Task EmptyBearer_OnMcp_Is401WithTheSameChallengeAsNoToken(string header)
    {
        await using var host = await TestHost.StartAsync();

        var empty = await host.PostMcpAsync(header);
        var none = await host.PostMcpAsync(null);

        empty.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        empty.Headers.WwwAuthenticate.ToString().Should()
            .Be(none.Headers.WwwAuthenticate.ToString())
            .And.Be($"Bearer resource_metadata=\"{PublicBaseUrl}/.well-known/oauth-protected-resource\"");
    }

    [Test]
    public async Task MalformedBearer_WithoutJsonAccept_IsStill401_NotPassedToTheHostChallenge()
    {
        await using var host = await TestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/beacon/mcp");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer not-a-jwt");
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

        var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().Should().Contain("error=\"invalid_token\"",
            "the JWT middleware answers on the MCP route itself, so a host's login redirect never sees the request");
        host.EndpointHits.Should().Be(0);
    }

    [Test]
    public async Task ValidBearer_ReachesTheEndpoint()
    {
        await using var host = await TestHost.StartAsync();

        var response = await host.PostMcpAsync($"Bearer {MintHmacToken()}");

        response.StatusCode.Should().Be(HttpStatusCode.OK, "the hardening must not reject a well-formed, valid token");
    }

    [Test]
    public async Task UnreachableJwks_OnMcp_Is503WithRetryAfter_Not401()
    {
        await using var host = await TestHost.StartAsync(jwksUnreachable: true);
        using var key = RSA.Create(2048);
        var credentials = new SigningCredentials(new RsaSecurityKey(key) { KeyId = "k1" }, SecurityAlgorithms.RsaSha256);
        var token = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            Issuer, Audience, [new Claim("sub", "s")], DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10), credentials));

        var response = await host.PostMcpAsync($"Bearer {token}");

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "the token was never judged; a 401 would make the client discard it and re-run OAuth for nothing");
        response.Headers.RetryAfter.Should().NotBeNull();
        response.Headers.WwwAuthenticate.Should().BeEmpty();
    }

    [Test]
    public async Task NoConfig_KeepsTheLegacyDocument()
    {
        await using var host = await TestHost.StartAsync();

        var metadata = await host.GetJsonAsync("/.well-known/oauth-protected-resource");
        var card = await host.GetJsonAsync("/.well-known/mcp/server-card.json");

        metadata.ContainsKey("authorization_servers").Should().BeFalse("no Beacon OIDC and nothing configured");
        Strings(metadata["scopes_supported"]).Should().Equal("Execute", "Admin");
        Strings(card["authentication"]!["scopes_supported"]).Should().Equal("Execute", "Admin");
        metadata["resource"]!.GetValue<string>().Should().Be(card["endpoint"]!.GetValue<string>());
    }

    private static string ResourceMetadataUrl(string challenge)
    {
        const string marker = "resource_metadata=\"";
        var start = challenge.IndexOf(marker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, "the challenge carries resource_metadata");
        start += marker.Length;

        return challenge[start..challenge.IndexOf('"', start)];
    }

    private static List<string> Strings(JsonNode? node) => node!.AsArray().Select(x => x!.GetValue<string>()).ToList();

    private static string MintHmacToken()
    {
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(SigningKey)), SecurityAlgorithms.HmacSha256);

        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(
            Issuer, Audience, [new Claim("sub", "s")], DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(10), credentials));
    }

    private sealed class TestHost : IAsyncDisposable
    {
        private readonly WebApplication _app;
        private readonly int[] _endpointHits;

        private TestHost(WebApplication app, List<string> logs, int[] endpointHits)
        {
            _app = app;
            _endpointHits = endpointHits;
            Logs = logs;
            Client = app.GetTestClient();
        }

        public HttpClient Client { get; }

        public List<string> Logs { get; }

        public int EndpointHits => Volatile.Read(ref _endpointHits[0]);

        public static async Task<TestHost> StartAsync(bool jwksUnreachable = false, Dictionary<string, string?>? settings = null)
        {
            var logs = new List<string>();
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Logging.SetMinimumLevel(LogLevel.Trace);
            builder.Logging.AddProvider(new CapturingLoggerProvider(logs));
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Beacon:PublicBaseUrl"] = PublicBaseUrl
            });
            if (settings != null)
            {
                builder.Configuration.AddInMemoryCollection(settings);
            }

            builder.Services.AddRouting();
            builder.Services.AddSingleton(new HttpClient(new UnreachableHandler()));
            builder.Services.AddSingleton(Mock.Of<IMcpCallerMapper>());
            builder.Services.AddBeaconJwtAuthentication(x =>
            {
                x.EnableBearerAuthentication = true;
                x.Validation.ValidIssuers = [V1Issuer, Issuer];
                x.Validation.ValidAudiences = [Audience, $"api://{Audience}"];
                if (jwksUnreachable)
                {
                    // Unique per test: the provider's JWKS cache is process-wide and keyed by endpoint.
                    x.Validation.JwksEndpoint = $"https://jwks.invalid/{Guid.NewGuid():N}/keys";
                }
                else
                {
                    x.Validation.SigningKey = SigningKey;
                }
            });

            var app = builder.Build();
            var endpointHits = new int[1];
            app.UseMcpResourceMetadataChallenge();
            app.UseBeaconJwtBearerAuthentication();
            app.MapMcpDiscovery();

            // Stand-in for the Execute-scope policy: anonymous → 401 with no header of its own.
            app.MapPost(McpDiscoveryPaths.McpPath, (HttpContext context) =>
            {
                Interlocked.Increment(ref endpointHits[0]);
                return context.User.Identity?.IsAuthenticated == true ? Results.Ok() : Results.Unauthorized();
            });

            await app.StartAsync();
            return new TestHost(app, logs, endpointHits);
        }

        public Task<HttpResponseMessage> PostMcpAsync(string? authorization)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, McpDiscoveryPaths.McpPath);
            request.Headers.TryAddWithoutValidation("Accept", "application/json, text/event-stream");
            if (authorization != null)
            {
                request.Headers.TryAddWithoutValidation("Authorization", authorization);
            }

            return Client.SendAsync(request);
        }

        public async Task<JsonObject> GetJsonAsync(string path)
        {
            var response = await Client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"{path} is anonymous");

            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!.AsObject();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
        }
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            throw new HttpRequestException("JWKS endpoint unreachable (test)");
        }
    }

    private sealed class CapturingLoggerProvider(List<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(logs);

        public void Dispose()
        {
        }

        private sealed class CapturingLogger(List<string> logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (logs)
                {
                    logs.Add($"{logLevel}: {formatter(state, exception)} {exception}");
                }
            }
        }
    }
}
