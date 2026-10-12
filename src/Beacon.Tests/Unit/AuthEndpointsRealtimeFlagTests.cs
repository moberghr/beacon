using System.Security.Claims;
using Beacon.Api;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Mcp;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// `/beacon/api/auth/me` is the only channel telling the React shell whether to open a hub
/// connection. If the flag stops reflecting <see cref="BeaconApiOptions.Realtime"/>, a host with
/// realtime off gets a client that polls a route which is not mapped.
/// </summary>
[TestFixture]
public class AuthEndpointsRealtimeFlagTests
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task GetCurrentUser_Authenticated_ReportsConfiguredRealtimeFlag(bool realtime)
    {
        var userContext = new Mock<IBeaconUserContext>();
        userContext.SetupGet(x => x.IsAuthenticated).Returns(true);
        userContext.SetupGet(x => x.UserId).Returns("42");
        userContext.SetupGet(x => x.UserName).Returns("mirko");
        userContext.SetupGet(x => x.DisplayName).Returns("Mirko");
        userContext.SetupGet(x => x.Email).Returns("mirko@example.test");

        // A host with Beacon's configuration, neither authorization nor user management on: the hub admits every
        // signed-in caller.
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")], "test")),
            RequestServices = new ServiceCollection().AddSingleton(new BeaconConfiguration()).BuildServiceProvider()
        };

        var response = await AuthEndpoints.GetCurrentUser(
            userContext.Object,
            httpContext,
            new BeaconApiOptions { Realtime = realtime });

        response.RealtimeEnabled.Should().Be(realtime);
        response.IsAuthenticated.Should().BeTrue();
        response.UserId.Should().Be("42");
        response.Roles.Should().ContainSingle().Which.Should().Be("Admin");
        response.SignOutEverywhereEnabled.Should().BeFalse("without user management no session is checked against a stamp");
    }

    [Test]
    public async Task GetCurrentUser_WithoutBeaconsConfiguration_ReportsNoRealtime()
    {
        var userContext = new Mock<IBeaconUserContext>();
        userContext.SetupGet(x => x.IsAuthenticated).Returns(true);
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")], "test"))
        };

        var response = await AuthEndpoints.GetCurrentUser(userContext.Object, httpContext, new BeaconApiOptions { Realtime = true });

        response.RealtimeEnabled.Should().BeFalse("the hub admits nobody on a host without Beacon's configuration");
    }

    [TestCase("session", true)]
    [TestCase("api-key", false)]
    [TestCase("no-id", false)]
    public async Task GetCurrentUser_TellsASignedInSessionWhetherItCanSignOutEverywhere(string caller, bool enabled)
    {
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.Enabled = true;
        var userContext = new Mock<IBeaconUserContext>();
        userContext.SetupGet(x => x.IsAuthenticated).Returns(true);
        var claims = caller switch
        {
            "session" => new[] { new Claim(ClaimTypes.NameIdentifier, "ext-ana") },
            "api-key" => [new Claim(ClaimTypes.NameIdentifier, "7"), new Claim(McpCallerClaimTypes.AuthMethod, McpCallerClaimTypes.ApiKeyAuthMethod)],
            _ => []
        };
        var authorization = new Mock<IBeaconAuthorizationProvider>();
        authorization.Setup(x => x.HasReadPermissionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var authenticationType = caller == "api-key" ? McpCallerClaimTypes.ApiKeyAuthenticationType : "test";
        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType)),
            RequestServices = new ServiceCollection()
                .AddSingleton(configuration)
                .AddSingleton(authorization.Object)
                .BuildServiceProvider()
        };

        var response = await AuthEndpoints.GetCurrentUser(userContext.Object, httpContext, new BeaconApiOptions());

        response.SignOutEverywhereEnabled.Should().Be(enabled);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task GetCurrentUser_Anonymous_StillReportsRealtimeFlag(bool realtime)
    {
        // The shell reads /auth/me before sign-in too, so the anonymous shape must carry the flag —
        // this is why CurrentUserResponse.Anonymous is a factory rather than a static singleton.
        var userContext = new Mock<IBeaconUserContext>();
        userContext.SetupGet(x => x.IsAuthenticated).Returns(false);

        var response = await AuthEndpoints.GetCurrentUser(
            userContext.Object,
            new DefaultHttpContext(),
            new BeaconApiOptions { Realtime = realtime });

        response.RealtimeEnabled.Should().Be(realtime);
        response.IsAuthenticated.Should().BeFalse();
        response.UserId.Should().BeNull();
        response.Roles.Should().BeEmpty();
    }
}
