using System.Security.Claims;
using Beacon.Api.Authentication;
using Beacon.Core;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The login-form redirect for a UI mounted under a base path: a signed-out browser is sent to {basePath}/login, the
/// login page itself is let through (or the browser is redirected to it forever), and an endpoint's metadata decides the
/// rest: an anonymous endpoint at the exact path it is mapped at, or an endpoint with an authorization requirement.
/// </summary>
[TestFixture]
public class LoginFormAuthMiddlewareTests
{
    private const string BasePath = "/beacon";

    [TestCase("/beacon/login")]
    [TestCase("/login")]
    public async Task AnonymousRequest_ToTheLoginPage_OrOutsideBeacon_IsLetThrough(string path)
    {
        var (context, nextCalled) = await RunAsync(path, authenticated: false);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [TestCase("/beacon/logout")]
    [TestCase("/beacon/error")]
    [TestCase("/beacon/api/auth/providers")]
    public async Task AnonymousRequest_ToAnAnonymousEndpoint_AtItsMappedPath_IsLetThrough(string path)
    {
        var (_, nextCalled) = await RunAsync(path, authenticated: false, endpoint: Route(path, new AllowAnonymousAttribute()));

        nextCalled.Should().BeTrue();
    }

    [TestCase("/beacon/logout", "/beacon/LOGOUT")]
    [TestCase("/beacon/error", "/BEACON/error")]
    [TestCase("/beacon/error", "/beacon/error/")]
    public async Task AnonymousRequest_ToAnAnonymousEndpoint_AtADifferentlyCasedPath_IsRedirectedToLogin(string mapped, string path)
    {
        var (context, nextCalled) = await RunAsync(path, authenticated: false, endpoint: Route(mapped, new AllowAnonymousAttribute()));

        nextCalled.Should().BeFalse();
        context.Response.Headers.Location.ToString().Should().Be("/beacon/login");
    }

    [Test]
    public async Task AnonymousRequest_ToAnEndpointWithAnAuthorizationRequirement_IsLeftToIt()
    {
        var (_, nextCalled) = await RunAsync("/beacon/home", authenticated: false, endpoint: Route("/beacon/{**page}", new AuthorizeAttribute("BeaconUiPage")));

        nextCalled.Should().BeTrue("UseAuthorization has already answered for the endpoint");
    }

    [TestCase("/beacon/logout")]
    [TestCase("/beacon/error")]
    [TestCase("/beacon/home")]
    public async Task AnonymousRequest_ToAPageWithoutAuthorizationMetadata_IsRedirectedToLogin(string path)
    {
        var (context, nextCalled) = await RunAsync(path, authenticated: false, endpoint: Route("/beacon/{**page}"));

        nextCalled.Should().BeFalse();
        context.Response.Headers.Location.ToString().Should().Be("/beacon/login");
    }

    [TestCase(true, true)]
    [TestCase(false, false)]
    public async Task AnonymousRequest_ToTheSetupPage_IsLetThroughOnlyWhileBeaconManagesUsers(bool userManagement, bool letThrough)
    {
        var configuration = CreateConfiguration();
        configuration.UserManagement.Enabled = userManagement;

        var (_, nextCalled) = await RunAsync("/beacon/setup", authenticated: false, configuration);

        nextCalled.Should().Be(letThrough);
    }

    [Test]
    public async Task AnonymousRequest_ToTheLoginPage_IsNotRedirectedToItself()
    {
        var (context, _) = await RunAsync("/beacon/login", authenticated: false);

        context.Response.Headers.Location.ToString().Should().BeEmpty();
    }

    [Test]
    public async Task AnonymousRequest_ToAnotherUiPage_IsRedirectedToTheBasePathLogin()
    {
        var (context, nextCalled) = await RunAsync("/beacon/home", authenticated: false);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
        context.Response.Headers.Location.ToString().Should().Be("/beacon/login");
    }

    [TestCase("/BEACON/LOGIN")]
    [TestCase("/beacon/login/extra")]
    [TestCase("/beacon/loginx")]
    public async Task AnonymousRequest_ToALookAlikePath_IsRedirectedToLogin(string path)
    {
        var (context, nextCalled) = await RunAsync(path, authenticated: false);

        nextCalled.Should().BeFalse();
        context.Response.Headers.Location.ToString().Should().Be("/beacon/login");
    }

    [Test]
    public async Task AuthenticatedRequest_ToAUiPage_IsLetThrough()
    {
        var (_, nextCalled) = await RunAsync("/beacon/home", authenticated: true);

        nextCalled.Should().BeTrue();
    }

    [Test]
    public async Task CustomLoginPath_UnderTheBasePath_IsLetThrough()
    {
        var configuration = CreateConfiguration();
        configuration.Authentication.LoginPath = "/sign-in";

        var (context, nextCalled) = await RunAsync("/beacon/sign-in", authenticated: false, configuration);

        nextCalled.Should().BeTrue();
        context.Response.Headers.Location.ToString().Should().BeEmpty();
    }

    [Test]
    public async Task EmptyLoginPath_DoesNotLetTheBarePathThrough()
    {
        var configuration = CreateConfiguration();
        configuration.Authentication.LoginPath = "";

        var (_, nextCalled) = await RunAsync("/beacon", authenticated: false, configuration);

        nextCalled.Should().BeFalse();
    }

    private static BeaconConfiguration CreateConfiguration()
    {
        var configuration = new BeaconConfiguration();
        configuration.Authentication.EnableLoginForm = true;
        return configuration;
    }

    private static RouteEndpoint Route(string pattern, params object[] metadata)
    {
        return new RouteEndpoint(_ => Task.CompletedTask, RoutePatternFactory.Parse(pattern), 0, new EndpointMetadataCollection(metadata), pattern);
    }

    private static async Task<(HttpContext Context, bool NextCalled)> RunAsync(
        string path,
        bool authenticated,
        BeaconConfiguration? configuration = null,
        Endpoint? endpoint = null)
    {
        var nextCalled = false;
        var middleware = new LoginFormAuthMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            configuration ?? CreateConfiguration(),
            BasePath);

        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.SetEndpoint(endpoint);
        context.User = authenticated
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "user")], "Test"))
            : new ClaimsPrincipal(new ClaimsIdentity());

        await middleware.InvokeAsync(context);
        return (context, nextCalled);
    }
}
