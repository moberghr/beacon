using System.Security.Claims;
using Beacon.Api.Authentication;
using Beacon.Core;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The login-form redirect for a UI mounted under a base path: a signed-out browser is sent to {basePath}/login, and
/// that page (with logout and error) must itself be let through, or the browser is redirected to it forever.
/// </summary>
[TestFixture]
public class LoginFormAuthMiddlewareTests
{
    private const string BasePath = "/beacon";

    [TestCase("/beacon/login")]
    [TestCase("/beacon/logout")]
    [TestCase("/beacon/error")]
    [TestCase("/login")]
    public async Task AnonymousRequest_ToAnAuthLandingPage_IsLetThrough(string path)
    {
        var (context, nextCalled) = await RunAsync(path, authenticated: false);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
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

    [TestCase("/beacon/api/projects")]
    [TestCase("/beacon/api/hub/negotiate")]
    [TestCase("/beacon/_content/Beacon.UI/app.bin")]
    public async Task AnonymousRequest_ToAnApiOrStaticSegment_IsLetThrough(string path)
    {
        var (_, nextCalled) = await RunAsync(path, authenticated: false);

        nextCalled.Should().BeTrue();
    }

    [TestCase("/beacon/apix/projects")]
    [TestCase("/beacon/pages/api/projects")]
    [TestCase("/beacon/pages/_content/app.bin")]
    public async Task AnonymousRequest_ToAPathThatOnlyContainsAnAllowedSegment_IsRedirectedToLogin(string path)
    {
        var (context, nextCalled) = await RunAsync(path, authenticated: false);

        nextCalled.Should().BeFalse();
        context.Response.Headers.Location.ToString().Should().Be("/beacon/login");
    }

    [Test]
    public async Task AnonymousRequest_ToAPathThatOnlyContainsSetup_IsRedirectedToLogin()
    {
        var configuration = CreateConfiguration();
        configuration.EnableUserManagement();

        var (setup, setupReached) = await RunAsync("/beacon/setup", authenticated: false, configuration);
        var (lookAlike, lookAlikeReached) = await RunAsync("/beacon/reports/setup-notes", authenticated: false, configuration);

        setupReached.Should().BeTrue();
        setup.Response.Headers.Location.ToString().Should().BeEmpty();
        lookAlikeReached.Should().BeFalse();
        lookAlike.Response.Headers.Location.ToString().Should().Be("/beacon/login");
    }

    private static BeaconConfiguration CreateConfiguration()
    {
        var configuration = new BeaconConfiguration();
        configuration.Authentication.EnableLoginForm = true;
        return configuration;
    }

    private static async Task<(HttpContext Context, bool NextCalled)> RunAsync(
        string path,
        bool authenticated,
        BeaconConfiguration? configuration = null)
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
        context.User = authenticated
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "user")], "Test"))
            : new ClaimsPrincipal(new ClaimsIdentity());

        await middleware.InvokeAsync(context);
        return (context, nextCalled);
    }
}
