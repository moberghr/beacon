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
    public void GetCurrentUser_ExternalLoginConfigured_ReportsLoginUrlAndParameter(bool authenticated)
    {
        var options = new BeaconApiOptions
        {
            ExternalLogin = new ExternalLoginOptions { LoginUrl = "/Account/Login", ReturnUrlParameter = "next" }
        };

        var response = AuthEndpoints.GetCurrentUser(CreateUserContext(authenticated), new DefaultHttpContext(), options);

        response.IsAuthenticated.Should().Be(authenticated);
        response.ExternalLogin.Should().Be(new ExternalLoginResponse("/Account/Login", "next"));
    }

    [Test]
    public void GetCurrentUser_ExternalLoginDefaultParameter_IsReturnUrl()
    {
        var options = new BeaconApiOptions { ExternalLogin = new ExternalLoginOptions { LoginUrl = "/Account/Login" } };

        var response = AuthEndpoints.GetCurrentUser(CreateUserContext(false), new DefaultHttpContext(), options);

        response.ExternalLogin!.ReturnUrlParameter.Should().Be("returnUrl");
    }

    [Test]
    public void GetCurrentUser_NoExternalLogin_ReportsNull()
    {
        var response = AuthEndpoints.GetCurrentUser(CreateUserContext(false), new DefaultHttpContext(), new BeaconApiOptions());

        response.ExternalLogin.Should().BeNull("without the option the shell keeps Beacon's own login page");
    }

    private static IBeaconUserContext CreateUserContext(bool authenticated)
    {
        var userContext = new Mock<IBeaconUserContext>();
        userContext.SetupGet(x => x.IsAuthenticated).Returns(authenticated);
        userContext.SetupGet(x => x.UserId).Returns(authenticated ? "42" : null);

        return userContext.Object;
    }
}
