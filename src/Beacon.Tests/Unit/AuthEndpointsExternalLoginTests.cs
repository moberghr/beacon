using Beacon.Api;
using Beacon.Api.Endpoints;
using Beacon.Core.Authorization;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// A host that owns sign-in must reach the shell through `/beacon/api/auth/me` — the anonymous shape
/// above all, because that is the moment the shell decides where to send the user instead of Beacon's
/// own login page.
/// </summary>
[TestFixture]
public class AuthEndpointsExternalLoginTests
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task GetCurrentUser_ExternalLoginConfigured_ReportsLoginUrlAndParameter(bool authenticated)
    {
        var options = new BeaconApiOptions
        {
            ExternalLogin = new ExternalLoginOptions { LoginUrl = "/Account/Login", ReturnUrlParameter = "next" }
        };

        var response = await AuthEndpoints.GetCurrentUser(CreateUserContext(authenticated), new DefaultHttpContext(), options);

        response.IsAuthenticated.Should().Be(authenticated);
        response.ExternalLogin.Should().Be(new ExternalLoginResponse("/Account/Login", "next"));
    }

    [Test]
    public async Task GetCurrentUser_ExternalLoginDefaultParameter_IsReturnUrl()
    {
        var options = new BeaconApiOptions { ExternalLogin = new ExternalLoginOptions { LoginUrl = "/Account/Login" } };

        var response = await AuthEndpoints.GetCurrentUser(CreateUserContext(false), new DefaultHttpContext(), options);

        response.ExternalLogin!.ReturnUrlParameter.Should().Be("returnUrl");
    }

    [Test]
    public async Task GetCurrentUser_NoExternalLogin_ReportsNull()
    {
        var response = await AuthEndpoints.GetCurrentUser(CreateUserContext(false), new DefaultHttpContext(), new BeaconApiOptions());

        response.ExternalLogin.Should().BeNull("without the option the shell keeps Beacon's own login page");
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task GetCurrentUser_GatewayMode_IsReported_SignedInOrNot(bool authenticated)
    {
        var options = new BeaconApiOptions { GatewayMode = true };

        var response = await AuthEndpoints.GetCurrentUser(CreateUserContext(authenticated), new DefaultHttpContext(), options);

        response.IsAuthenticated.Should().Be(authenticated);
        response.GatewayMode.Should().BeTrue("the shell keeps a signed-out visitor of the gateway away from pages it does not serve");
    }

    [Test]
    public async Task GetCurrentUser_NoGateway_ReportsFalse()
    {
        var response = await AuthEndpoints.GetCurrentUser(CreateUserContext(false), new DefaultHttpContext(), new BeaconApiOptions());

        response.GatewayMode.Should().BeFalse();
    }

    private static IBeaconUserContext CreateUserContext(bool authenticated)
    {
        var userContext = new Mock<IBeaconUserContext>();
        userContext.SetupGet(x => x.IsAuthenticated).Returns(authenticated);
        userContext.SetupGet(x => x.UserId).Returns(authenticated ? "42" : null);

        return userContext.Object;
    }
}
