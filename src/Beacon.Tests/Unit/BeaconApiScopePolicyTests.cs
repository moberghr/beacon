using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Beacon.Api.Endpoints;

namespace Beacon.Tests.Unit;

/// <summary>
/// §1.4 — the Execute-scope policy must deny a Read-scoped API key while letting Execute/Admin
/// keys and interactive cookie/OIDC sessions through. Guards the assertion logic in
/// <see cref="BeaconApiEndpoints.AddBeaconApiAuthorization"/>.
/// </summary>
[TestFixture]
public class BeaconApiScopePolicyTests
{
    private IAuthorizationService _authService = null!;

    [SetUp]
    public void SetUp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddBeaconApiAuthorization();
        _authService = services.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
    }

    private static ClaimsPrincipal ApiKey(params string[] scopes)
    {
        var claims = new List<Claim> { new("auth_method", "api_key") };
        claims.AddRange(scopes.Select(x => new Claim("scope", x)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "ApiKey"));
    }

    private static ClaimsPrincipal McpJwtCaller(params string[] scopes)
    {
        var claims = new List<Claim> { new("auth_method", "mcp_caller") };
        claims.AddRange(scopes.Select(x => new Claim("scope", x)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "Bearer"));
    }

    private Task<AuthorizationResult> AuthorizeAsync(ClaimsPrincipal user) =>
        _authService.AuthorizeAsync(user, resource: null, BeaconApiEndpoints.ExecuteScopePolicyName);

    [Test]
    public async Task ReadScopedApiKey_IsDenied()
    {
        var result = await AuthorizeAsync(ApiKey("Read"));

        result.Succeeded.Should().BeFalse();
    }

    [Test]
    public async Task ExecuteScopedApiKey_IsAllowed()
    {
        var result = await AuthorizeAsync(ApiKey("Execute"));

        result.Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task AdminScopedApiKey_IsAllowed()
    {
        var result = await AuthorizeAsync(ApiKey("Admin"));

        result.Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task ApiKeyWithReadAndExecute_IsAllowed()
    {
        var result = await AuthorizeAsync(ApiKey("Read", "Execute"));

        result.Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task CookieSession_HasNoScopeClaim_PassesThrough()
    {
        var cookie = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "42"), new Claim(ClaimTypes.Role, "Editor")],
            authenticationType: "Cookies"));

        var result = await AuthorizeAsync(cookie);

        result.Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task ReadScopedMcpJwtCaller_IsDenied()
    {
        var result = await AuthorizeAsync(McpJwtCaller("Read"));

        result.Succeeded.Should().BeFalse("a Read-scoped system or user must not reach MCP (§1.4)");
    }

    [Test]
    public async Task ExecuteScopedMcpJwtCaller_IsAllowed()
    {
        var result = await AuthorizeAsync(McpJwtCaller("Execute"));

        result.Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task AdminScopedMcpJwtCaller_IsAllowed()
    {
        var result = await AuthorizeAsync(McpJwtCaller("Admin"));

        result.Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task UnmappedMcpJwtCaller_WithNoScope_IsDenied()
    {
        var result = await AuthorizeAsync(McpJwtCaller());

        result.Succeeded.Should().BeFalse("an unknown JWT caller on /beacon/mcp fails closed with 403");
    }

    [Test]
    public async Task JwtCallerOutsideMcp_IsNotScopeGated()
    {
        var jwt = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "sub-1"), new Claim("auth_method", "jwt")],
            authenticationType: "Bearer"));

        var result = await AuthorizeAsync(jwt);

        result.Succeeded.Should().BeTrue("non-MCP JWT behaviour is unchanged");
    }

    [Test]
    public async Task UnauthenticatedCaller_IsDenied()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await AuthorizeAsync(anonymous);

        result.Succeeded.Should().BeFalse();
    }
}
