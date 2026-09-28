using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Beacon.Api.Endpoints;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC10 — GET /beacon/api/mcp/audit is gated by <see cref="BeaconApiEndpoints.AdminPolicyName"/>. DB-free: the
/// policy is resolved from the same <see cref="BeaconApiEndpoints.AddBeaconApiAuthorization"/> registration the
/// host uses, and evaluated against the principal shapes that reach the endpoint.
/// </summary>
[TestFixture]
public class McpAuditAdminPolicyTests
{
    private IAuthorizationService _authService = null!;
    private ServiceProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBeaconApiAuthorization();
        _provider = services.BuildServiceProvider();
        _authService = _provider.GetRequiredService<IAuthorizationService>();
    }

    [TearDown]
    public void TearDown()
    {
        _provider.Dispose();
    }

    [Test]
    public async Task AuthenticatedNonAdmin_IsDenied()
    {
        var user = Principal("Cookies", new Claim(ClaimTypes.NameIdentifier, "5"), new Claim(ClaimTypes.Role, "User"));

        var result = await AuthorizeAsync(user);

        result.Succeeded.Should().BeFalse();
    }

    [Test]
    public async Task AdminRole_IsAllowed()
    {
        var user = Principal("Cookies", new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Role, "Admin"));

        var result = await AuthorizeAsync(user);

        result.Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task ExecuteScopedApiKey_IsDenied()
    {
        var user = Principal("ApiKey", new Claim("auth_method", "api_key"), new Claim("scope", "Execute"),
            new Claim("api_key_id", "9"));

        var result = await AuthorizeAsync(user);

        result.Succeeded.Should().BeFalse("an Execute-scoped API key is not an admin and must not export the audit");
    }

    [Test]
    public async Task Anonymous_IsDenied()
    {
        var result = await AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity()));

        result.Succeeded.Should().BeFalse();
    }

    private Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user) =>
        _authService.AuthorizeAsync(user, resource: null, BeaconApiEndpoints.AdminPolicyName);

    private static ClaimsPrincipal Principal(string authenticationType, params Claim[] claims) =>
        new(new ClaimsIdentity(claims, authenticationType));
}
