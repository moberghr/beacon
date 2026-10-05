using System.Security.Claims;
using Beacon.Api.Endpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The React shell always sends its token in <c>X-XSRF-TOKEN</c>, but antiforgery options belong to the host.
/// A host that keeps the default header (<c>RequestVerificationToken</c>) — e.g. an MVC admin app that mounts
/// Beacon — must still accept SPA mutations. Runs the real <see cref="IAntiforgery"/>, not a mock.
/// </summary>
[TestFixture]
public class AntiforgeryEndpointFilterTests
{
    private static readonly ClaimsPrincipal User = new(new ClaimsIdentity([new Claim(ClaimTypes.Name, "alice")], "Cookies"));

    [Test]
    public async Task Post_WithSpaHeader_PassesOnHostWithDefaultHeaderName()
    {
        using var services = BuildServices(x => x.Cookie.Name = "ng.admin.antiforgery");
        var context = CreatePostWithSpaToken(services);

        var result = await InvokeFilter(services, context);

        result.Should().Be("next", "the SPA's X-XSRF-TOKEN must satisfy a host that kept RequestVerificationToken");
    }

    [Test]
    public async Task Post_WithSpaHeader_PassesOnHostConfiguredForSpaHeader()
    {
        using var services = BuildServices(x => x.HeaderName = AntiforgeryEndpointFilter.SpaHeaderName);
        var context = CreatePostWithSpaToken(services);

        var result = await InvokeFilter(services, context);

        result.Should().Be("next");
    }

    [Test]
    public async Task Post_WithSpaHeaderFromAnotherIdentity_IsRejected()
    {
        using var services = BuildServices(x => x.Cookie.Name = "ng.admin.antiforgery");
        var context = CreatePostWithSpaToken(services);
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "mallory")], "Cookies"));

        var result = await InvokeFilter(services, context);

        result.Should().BeOfType<ProblemHttpResult>("bridging the header name must not skip token validation")
            .Which.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
    }

    [Test]
    public async Task Post_WithoutToken_IsRejected()
    {
        using var services = BuildServices(x => x.Cookie.Name = "ng.admin.antiforgery");
        var context = CreatePostWithSpaToken(services);
        context.Request.Headers.Remove(AntiforgeryEndpointFilter.SpaHeaderName);

        var result = await InvokeFilter(services, context);

        result.Should().BeOfType<ProblemHttpResult>()
            .Which.ProblemDetails.Detail.Should().Contain("RequestVerificationToken");
    }

    private static ServiceProvider BuildServices(Action<AntiforgeryOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAntiforgery(configure);

        return services.BuildServiceProvider();
    }

    // Mints a token pair the way GET /beacon/api/csrf does, then builds the POST the SPA sends:
    // host antiforgery cookie + the request token in X-XSRF-TOKEN.
    private static DefaultHttpContext CreatePostWithSpaToken(IServiceProvider services)
    {
        var antiforgery = services.GetRequiredService<IAntiforgery>();
        var mintContext = new DefaultHttpContext { RequestServices = services, User = User };
        var tokens = antiforgery.GetAndStoreTokens(mintContext);
        var cookieName = services.GetRequiredService<IOptions<AntiforgeryOptions>>().Value.Cookie.Name;

        var context = new DefaultHttpContext { RequestServices = services, User = User };
        context.Request.Method = HttpMethods.Post;
        context.Request.Headers.Cookie = $"{cookieName}={tokens.CookieToken}";
        context.Request.Headers[AntiforgeryEndpointFilter.SpaHeaderName] = tokens.RequestToken;

        return context;
    }

    private static async Task<object?> InvokeFilter(IServiceProvider services, HttpContext context)
    {
        var filter = new AntiforgeryEndpointFilter(
            services.GetRequiredService<IAntiforgery>(),
            services.GetRequiredService<IOptions<AntiforgeryOptions>>(),
            NullLogger<AntiforgeryEndpointFilter>.Instance);
        var invocationContext = EndpointFilterInvocationContext.Create(context);

        return await filter.InvokeAsync(invocationContext, _ => ValueTask.FromResult<object?>("next"));
    }
}
