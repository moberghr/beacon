using System.Security.Claims;
using Beacon.Core.Authorization;
using Beacon.Core.Mcp;
using FluentAssertions;
using NUnit.Framework;

namespace Beacon.Tests.Unit.BackgroundTasks;

/// <summary>
/// The one id the realtime hub addresses a caller by, also the id a background job notifies: a signed-in session's (or a
/// bound bearer user's) external id; nothing for an API key or an MCP caller, whose numeric ids live in another id space.
/// </summary>
[TestFixture]
public class BeaconHubUserIdTests
{
    [Test]
    public void ASession_IsAddressedByItsExternalId()
    {
        var session = Principal("cookie", new Claim(ClaimTypes.NameIdentifier, "ext-ana"));

        BeaconHubUserId.Of(session).Should().Be("ext-ana");
    }

    [Test]
    public void ABoundBearerUser_IsAddressedByItsExternalId()
    {
        var bearer = Principal(
            "Bearer",
            new Claim(ClaimTypes.NameIdentifier, "ext-ana"),
            new Claim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.JwtAuthMethod));

        BeaconHubUserId.Of(bearer).Should().Be("ext-ana");
    }

    [Test]
    public void AnApiKey_IsAddressedByNothing()
    {
        var apiKey = Principal(
            McpCallerClaimTypes.ApiKeyAuthenticationType,
            new Claim(ClaimTypes.NameIdentifier, "42"),
            new Claim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.ApiKeyAuthMethod));

        BeaconHubUserId.Of(apiKey).Should().BeNull();
    }

    [Test]
    public void AnMcpCaller_IsAddressedByNothing()
    {
        var mcpCaller = Principal(
            "Bearer",
            new Claim(ClaimTypes.NameIdentifier, "42"),
            new Claim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.McpCallerAuthMethod));

        BeaconHubUserId.Of(mcpCaller).Should().BeNull();
    }

    [Test]
    public void AnAnonymousPrincipal_IsAddressedByNothing()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ext-ana")]));

        BeaconHubUserId.Of(anonymous).Should().BeNull();
        BeaconHubUserId.Of(null).Should().BeNull();
    }

    private static ClaimsPrincipal Principal(string authenticationType, params Claim[] claims)
        => new(new ClaimsIdentity(claims, authenticationType));
}
