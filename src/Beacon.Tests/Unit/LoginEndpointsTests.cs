using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;
using Beacon.Core.Authentication;
using Beacon.Api;
using Beacon.Api.Endpoints;
using Beacon.Api.Authentication;
using Beacon.Core;
using Beacon.UI;

namespace Beacon.Tests.Unit;

/// <summary>
/// Complements <see cref="SsoChallengeReturnUrlTests"/> for the post-cutover deployment
/// where the React shell is mounted at the root URL (<c>basePath = ""</c>). The SSO tests
/// cover the legacy <c>/beacon</c> base path; these cover the bare-root path and the
/// URL-encoded-but-safe inputs the SSO suite does not exercise.
/// </summary>
[TestFixture]
public class LoginEndpointsTests
{
    private const string RootBasePath = "";

    [TestCase("/projects")]
    [TestCase("/queries/123")]
    [TestCase("/projects?tab=overview")]
    [TestCase("/queries/123#results")]
    public void IsSafeReturnUrl_RelativePathUnderRoot_IsAccepted(string url)
    {
        LoginEndpoints.IsSafeReturnUrl(url, RootBasePath)
            .Should().BeTrue("relative SPA paths under '/' must be honoured after the React cutover");
    }

    [TestCase("/queries/q%2Fwith-slash")]
    [TestCase("/projects/Hello%20World")]
    public void IsSafeReturnUrl_UrlEncodedSafePath_IsAccepted(string url)
    {
        LoginEndpoints.IsSafeReturnUrl(url, RootBasePath)
            .Should().BeTrue("URL-encoded slashes/spaces inside the path must not be rejected");
    }

    [Test]
    public void IsSafeReturnUrl_NullReturnUrl_IsRejected()
    {
        LoginEndpoints.IsSafeReturnUrl(null, RootBasePath)
            .Should().BeFalse("a missing return URL must fall back to the configured login redirect");
    }

    [TestCase("")]
    [TestCase("   ")]
    public void IsSafeReturnUrl_EmptyOrWhitespace_IsRejected(string url)
    {
        LoginEndpoints.IsSafeReturnUrl(url, RootBasePath)
            .Should().BeFalse("empty / whitespace must not satisfy the safe-redirect predicate");
    }

    [TestCase("//evil.com/path")]
    [TestCase("/\\evil.com/path")]
    public void IsSafeReturnUrl_ProtocolRelativeOrBackslash_IsRejected(string url)
    {
        LoginEndpoints.IsSafeReturnUrl(url, RootBasePath)
            .Should().BeFalse("protocol-relative and backslash-prefixed URLs are classic open-redirect vectors");
    }

    [TestCase("https://evil.com")]
    [TestCase("http://evil.com/projects")]
    [TestCase("https://evil.com/queries/123")]
    public void IsSafeReturnUrl_AbsoluteUrl_IsRejected(string url)
    {
        LoginEndpoints.IsSafeReturnUrl(url, RootBasePath)
            .Should().BeFalse("absolute URLs must never be honoured as post-login destinations");
    }

    // The local-login response and the SSO fallback both send the browser to LoginRedirectPath with a
    // full page load, outside the SPA router, so it must already sit under the path the SPA is mounted at.
    [Test]
    public void LoginRedirectPath_DefaultsToHomeUnderTheUiBasePath()
    {
        new AuthenticationOptions().LoginRedirectPath
            .Should().Be($"{BeaconUiEndpointRouteBuilderExtensions.BasePath}/home");
    }

    [Test]
    public async Task Login_WithoutHostRateLimiter_AllowsTenThenThrottlesEleventhWithRetryAfter()
    {
        await using var app = await StartHostAsync(useHostRateLimiter: false, registerLimiter: true);
        using var client = app.GetTestClient();

        for (var i = 0; i < 10; i++)
        {
            var response = await PostLoginAsync(client, "10.0.0.1");
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"attempt {i + 1} is inside the 10/60 s window");
        }

        var throttled = await PostLoginAsync(client, "10.0.0.1");

        throttled.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        throttled.Headers.RetryAfter.Should().NotBeNull();
        throttled.Headers.RetryAfter!.Delta!.Value.TotalSeconds.Should().BeInRange(1, 60);
    }

    [Test]
    public async Task Login_DifferentRemoteIp_IsNotThrottledByAnotherIpsAttempts()
    {
        await using var app = await StartHostAsync(useHostRateLimiter: false, registerLimiter: true);
        using var client = app.GetTestClient();

        for (var i = 0; i < 11; i++)
        {
            await PostLoginAsync(client, "10.0.0.1");
        }

        var other = await PostLoginAsync(client, "10.0.0.2");

        other.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task Login_HostWithOwnRateLimiterAndNoLoginPolicy_StillAnswers()
    {
        await using var app = await StartHostAsync(useHostRateLimiter: true, registerLimiter: true);
        using var client = app.GetTestClient();

        var response = await PostLoginAsync(client, "10.0.0.3");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "a host limiter without a 'login' policy must not turn login into a 500");
    }

    [Test]
    public async Task Login_HostThatNeverRegisteredTheLimiter_FallsBackToProcessWideInstance()
    {
        await using var app = await StartHostAsync(useHostRateLimiter: false, registerLimiter: false);
        using var client = app.GetTestClient();
        var ip = "10.77.0.1";

        for (var i = 0; i < 10; i++)
        {
            (await PostLoginAsync(client, ip)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }

        (await PostLoginAsync(client, ip)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Test]
    public void AddBeaconApiServices_RealtimeOff_StillRegistersLoginRateLimiter()
    {
        var services = new ServiceCollection();

        services.AddBeaconApiServices(x => x.Realtime = false);

        services.Should().Contain(x => x.ServiceType == typeof(LoginRateLimiter));
    }

    [Test]
    public async Task SignOut_IsPostOnly_ThereIsNoAnonymousGetThatEndsASession()
    {
        await using var app = await StartHostAsync(useHostRateLimiter: false, registerLimiter: true);
        var client = app.GetTestClient();

        var signOutGet = await client.GetAsync("/beacon/api/auth/signout");
        var logoutGet = await client.GetAsync("/beacon/api/auth/logout");

        signOutGet.StatusCode.Should().Be(HttpStatusCode.NotFound);
        logoutGet.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        signOutGet.Headers.Contains("Set-Cookie").Should().BeFalse();
    }

    private static async Task<WebApplication> StartHostAsync(bool useHostRateLimiter, bool registerLimiter)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var authProvider = new Mock<IBeaconAuthenticationProvider>();
        authProvider
            .Setup(x => x.AuthenticateAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new AuthenticationResult { Success = false, ErrorMessage = "Invalid credentials." });

        builder.Services.AddSingleton(authProvider.Object);
        builder.Services.AddSingleton(new Mock<IAntiforgery>().Object);
        if (registerLimiter)
        {
            builder.Services.AddSingleton(new LoginRateLimiter());
        }

        if (useHostRateLimiter)
        {
            builder.Services.AddRateLimiter(x => x.RejectionStatusCode = StatusCodes.Status429TooManyRequests);
        }

        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Request.Headers.TryGetValue("X-Test-Ip", out var ip))
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
            }

            await next();
        });

        if (useHostRateLimiter)
        {
            app.UseRateLimiter();
        }

        app.MapLoginEndpoints("/beacon", new BeaconConfiguration());
        await app.StartAsync();

        return app;
    }

    private static Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string ip)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/beacon/api/auth/login")
        {
            Content = JsonContent.Create(new LoginRequest("user", "wrong"))
        };
        request.Headers.Add("X-Test-Ip", ip);

        return client.SendAsync(request);
    }
}
