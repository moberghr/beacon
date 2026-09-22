using System.Security.Claims;
using Beacon.Api;
using Beacon.Api.Endpoints;
using Beacon.Core.Authorization;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
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
    public void GetCurrentUser_Authenticated_ReportsConfiguredRealtimeFlag(bool realtime)
    {
        var userContext = new Mock<IBeaconUserContext>();
        userContext.SetupGet(x => x.IsAuthenticated).Returns(true);
        userContext.SetupGet(x => x.UserId).Returns("42");
        userContext.SetupGet(x => x.UserName).Returns("mirko");
        userContext.SetupGet(x => x.DisplayName).Returns("Mirko");
        userContext.SetupGet(x => x.Email).Returns("mirko@example.test");

        var httpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "Admin")], "test"))
        };

        var response = AuthEndpoints.GetCurrentUser(
            userContext.Object,
            httpContext,
            new BeaconApiOptions { Realtime = realtime });

        response.RealtimeEnabled.Should().Be(realtime);
        response.IsAuthenticated.Should().BeTrue();
        response.UserId.Should().Be("42");
        response.Roles.Should().ContainSingle().Which.Should().Be("Admin");
    }

    [TestCase(true)]
    [TestCase(false)]
    public void GetCurrentUser_Anonymous_StillReportsRealtimeFlag(bool realtime)
    {
        // The shell reads /auth/me before sign-in too, so the anonymous shape must carry the flag —
        // this is why CurrentUserResponse.Anonymous is a factory rather than a static singleton.
        var userContext = new Mock<IBeaconUserContext>();
        userContext.SetupGet(x => x.IsAuthenticated).Returns(false);

        var response = AuthEndpoints.GetCurrentUser(
            userContext.Object,
            new DefaultHttpContext(),
            new BeaconApiOptions { Realtime = realtime });

        response.RealtimeEnabled.Should().Be(realtime);
        response.IsAuthenticated.Should().BeFalse();
        response.UserId.Should().BeNull();
        response.Roles.Should().BeEmpty();
    }
}
