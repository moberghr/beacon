using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Integration.Api;

[TestFixture]
[Category("Phase1Harness")]
public class Phase1HarnessTests
{
    private BeaconWebApplicationFactory? _factory;
    private HttpClient? _client;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        try
        {
            _factory = new BeaconWebApplicationFactory();
            _client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
            });
        }
        catch (Exception ex)
        {
            Assert.Inconclusive(
                $"Beacon host failed to bootstrap: {ex.Message}. " +
                $"Set {BeaconWebApplicationFactory.TestConnectionStringEnvVar} to a reachable Postgres connection string.");
        }
    }

    [OneTimeTearDown]
    public void OneTimeTearDown()
    {
        _client?.Dispose();
        _factory?.Dispose();
    }

    [Test]
    public async Task Health_Anonymous_Returns200()
    {
        var response = await _client!.GetAsync("/beacon/api/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<HealthShape>();
        body!.Status.Should().Be("ok");
    }

    [Test]
    public async Task AuthMe_Anonymous_ReturnsIsAuthenticatedFalse()
    {
        var response = await _client!.GetAsync("/beacon/api/auth/me");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<AuthMeShape>();
        body!.IsAuthenticated.Should().BeFalse();
    }

    [Test]
    public async Task AuthPermissions_Anonymous_Returns401Json()
    {
        var response = await _client!.GetAsync("/beacon/api/auth/permissions");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    [Test]
    public async Task Csrf_Anonymous_ReturnsTokenAndSetsCookie()
    {
        var response = await _client!.GetAsync("/beacon/api/csrf");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CsrfShape>();
        body!.Token.Should().NotBeNullOrEmpty();

        response.Headers.TryGetValues("Set-Cookie", out var cookies).Should().BeTrue();
        cookies!.Any(x => x.StartsWith("XSRF-TOKEN=", StringComparison.Ordinal)).Should().BeTrue();
    }

    [Test]
    public async Task UnknownApiPath_Returns404()
    {
        var response = await _client!.GetAsync("/beacon/api/this-does-not-exist");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task Hub_Anonymous_Returns401Json()
    {
        // SignalR's negotiate endpoint is the first auth check — bypass returns 401 JSON
        // (per the cookie-auth OnRedirectToLogin override) instead of a 200 + websocket upgrade.
        var response = await _client!.PostAsync("/beacon/api/hub/negotiate?negotiateVersion=1", content: null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
    }

    private sealed record HealthShape(string Status);
    private sealed record AuthMeShape(bool IsAuthenticated);
    private sealed record CsrfShape(string Token);
}
