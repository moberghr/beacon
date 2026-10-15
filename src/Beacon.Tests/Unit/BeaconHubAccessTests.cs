using System.Net;
using System.Security.Claims;
using Beacon.Api;
using Beacon.Api.Endpoints;
using Beacon.Api.Hubs;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// The realtime hub admits a signed-in caller with read permission (Viewer or above, or an Admin), or one holding a
/// functional permission whose pages refresh from hub events (Approver, MigrationOperator); holders of other functional
/// permissions only, and callers with no role, get 403 at the negotiate request, and <c>/auth/me</c> tells them realtime
/// is off so the shell never tries. A host with neither authorization nor user management on admits every signed-in
/// caller.
/// </summary>
[TestFixture]
public class BeaconHubAccessTests
{
    private const string NegotiatePath = "/beacon/api/hub/negotiate?negotiateVersion=1";

    [TestCase(true, new string[0], true)]
    [TestCase(false, new[] { BeaconPermissions.Approver }, true)]
    [TestCase(false, new[] { BeaconPermissions.MigrationOperator }, true)]
    [TestCase(false, new[] { BeaconPermissions.UserAdmin }, false)]
    [TestCase(false, new[] { BeaconPermissions.Auditor, BeaconPermissions.SecurityAdmin, BeaconPermissions.DataSourceAdmin }, false)]
    [TestCase(false, new string[0], false)]
    public async Task TheHub_AdmitsReadersAndThePermissionsWhosePagesUseIt(bool canRead, string[] permissions, bool admitted)
    {
        var httpContext = Context(canRead, permissions, enforced: true);

        var allows = await BeaconHubAccess.AllowsAsync(httpContext);

        allows.Should().Be(admitted);
    }

    [Test]
    public async Task AFunctionalPermissionTheProviderVetoes_DoesNotAdmit()
    {
        var httpContext = Context(canRead: false, [BeaconPermissions.Approver], enforced: true, providerAllowsPermissions: false);

        var allows = await BeaconHubAccess.AllowsAsync(httpContext);

        allows.Should().BeFalse();
    }

    [Test]
    public async Task AHostWithoutAuthorizationOrUserManagement_AdmitsEverySignedInCaller()
    {
        var httpContext = Context(canRead: false, [], enforced: false);

        var allows = await BeaconHubAccess.AllowsAsync(httpContext);

        allows.Should().BeTrue();
    }

    // Authorization on without Beacon user management (a host's own provider) is enforced like user management.
    [TestCase(true, new string[0], true)]
    [TestCase(false, new[] { BeaconPermissions.Approver }, true)]
    [TestCase(false, new[] { BeaconPermissions.UserAdmin }, false)]
    [TestCase(false, new string[0], false)]
    public async Task WithAuthorizationOnly_TheHubAdmitsReadersAndThePermissionsWhosePagesUseIt(bool canRead, string[] permissions, bool admitted)
    {
        var httpContext = Context(canRead, permissions, enforced: false, authorizationOnly: true);

        var allows = await BeaconHubAccess.AllowsAsync(httpContext);

        allows.Should().Be(admitted);
    }

    [TestCase(true, new string[0], true)]
    [TestCase(false, new[] { BeaconPermissions.Approver }, true)]
    [TestCase(false, new[] { BeaconPermissions.UserAdmin }, false)]
    public async Task AuthMe_ReportsRealtimeOnlyToCallersTheHubAdmits(bool canRead, string[] permissions, bool realtime)
    {
        var httpContext = Context(canRead, permissions, enforced: true);
        var userContext = new Mock<IBeaconUserContext>();
        userContext.SetupGet(x => x.IsAuthenticated).Returns(true);

        var response = await AuthEndpoints.GetCurrentUser(userContext.Object, httpContext, new BeaconApiOptions { Realtime = true });

        response.RealtimeEnabled.Should().Be(realtime);
    }

    [TestCase("viewer", HttpStatusCode.OK)]
    [TestCase("approver", HttpStatusCode.OK)]
    [TestCase("user-admin", HttpStatusCode.Forbidden)]
    [TestCase("no-role", HttpStatusCode.Forbidden)]
    public async Task TheNegotiateRequest_IsRefusedToACallerTheHubDoesNotAdmit(string caller, HttpStatusCode expected)
    {
        await using var app = await StartHubHostAsync(caller);
        var client = app.GetTestClient();
        var signIn = await client.GetAsync("/test/sign-in");
        var cookie = signIn.Headers.GetValues("Set-Cookie").Single(x => x.StartsWith("Beacon.Auth=", StringComparison.Ordinal)).Split(';')[0];

        using var request = new HttpRequestMessage(HttpMethod.Post, NegotiatePath);
        request.Headers.Add("Cookie", cookie);
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(expected);
    }

    private static DefaultHttpContext Context(
        bool canRead,
        string[] permissions,
        bool enforced,
        bool providerAllowsPermissions = true,
        bool authorizationOnly = false)
    {
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.Enabled = enforced;
        configuration.Authorization.Enabled = authorizationOnly;
        var authorization = new Mock<IBeaconAuthorizationProvider>();
        authorization.Setup(x => x.HasReadPermissionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(canRead);
        authorization.Setup(x => x.AllowsFunctionalPermissionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(providerAllowsPermissions);

        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddSingleton(authorization.Object);
        services.AddSingleton(TestPermissions.Holding(permissions));

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ext-ana")], "Cookies"))
        };
    }

    // MapBeaconApi with realtime on over a test server, for a caller signed in with the Beacon.Auth cookie at
    // GET /test/sign-in who holds what <paramref name="caller"/> names now.
    private static async Task<WebApplication> StartHubHostAsync(string caller)
    {
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.Enabled = true;
        var authorization = new Mock<IBeaconAuthorizationProvider>();
        authorization.Setup(x => x.HasReadPermissionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(caller == "viewer");
        authorization.Setup(x => x.AllowsFunctionalPermissionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var permissions = caller switch
        {
            "approver" => TestPermissions.Holding(BeaconPermissions.Approver),
            "user-admin" => TestPermissions.Holding(BeaconPermissions.UserAdmin),
            _ => TestPermissions.None
        };

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton(new BeaconApiOptions { Realtime = true });
        builder.Services.AddSignalR();
        builder.Services.AddSingleton(Options.Create(new MigrationOptions()));
        builder.Services.AddSingleton(Mock.Of<IMediator>());
        builder.Services.AddSingleton(authorization.Object);
        builder.Services.AddSingleton(permissions);
        builder.Services.AddSingleton(Mock.Of<IBeaconUserContext>());
        builder.Services.AddSingleton(Mock.Of<IActorUserResolver>());
        builder.Services.AddSingleton(Mock.Of<IDbContextFactory<BeaconContext>>());
        builder.Services.AddAntiforgery();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddBeaconApiAuthorization();
        builder.Services
            .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(x =>
            {
                x.Cookie.Name = "Beacon.Auth";
                x.Events.OnRedirectToAccessDenied = y =>
                {
                    y.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
                x.Events.OnRedirectToLogin = y =>
                {
                    y.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return Task.CompletedTask;
                };
            });

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/test/sign-in", async (HttpContext context) =>
        {
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, "ext-ana"), new Claim(ClaimTypes.Name, "ana")],
                CookieAuthenticationDefaults.AuthenticationScheme));
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal);
        });
        app.MapBeaconApi();
        await app.StartAsync();

        return app;
    }
}
