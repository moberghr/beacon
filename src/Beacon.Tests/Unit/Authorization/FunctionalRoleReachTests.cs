using System.Net;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Api;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Authorization.Providers;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.Authorization;

/// <summary>
/// Each functional role, held with no Viewer or Editor role, reaches exactly its routes, and through them exactly the
/// exempt requests its policies gate; it reaches no project data, not even through a membership it holds; and over a
/// real request pipeline its holder is admitted to its routes and refused everywhere else.
/// </summary>
[TestFixture]
public class FunctionalRoleReachTests
{
    private const string TestUserHeader = "X-Test-User";
    private const string AntiforgeryCookieName = "test.antiforgery";

    private static IEnumerable<string> Permissions => BeaconPermissions.All;

    [TestCaseSource(nameof(Permissions))]
    public async Task EachFunctionalRole_ReachesExactlyItsRoutes(string permission)
    {
        var routes = await BeaconApiRoutes.ReadAsync();

        var reached = routes
            .Where(x => Reaches(x, permission))
            .Select(x => x.Key)
            .ToList();

        var expected = AdminGatedRoutesTests.PermissionPolicyByRoute
            .Where(x => BeaconPermissions.Policies[x.Value].Contains(permission))
            .Select(x => x.Key)
            .Concat(AdminGatedRoutesTests.AdmittingRoutes
                .Where(x => x.Value == permission)
                .Select(x => x.Key));
        reached.Should().BeEquivalentTo(expected);
    }

    [TestCaseSource(nameof(Permissions))]
    public async Task EachFunctionalRole_ReachesTheExemptRequestsItsPoliciesGate_AndNoAdminOnlyOne(string permission)
    {
        var routes = await BeaconApiRoutes.ReadAsync();
        var failures = new List<string>();

        foreach (var request in BeaconApiRoutes.RequestTypes())
        {
            var policy = BeaconApiRoutes.ExemptionOf(request)?.Policy;
            if (policy == null)
            {
                continue;
            }

            var gatedForThisRole = BeaconPermissions.Policies.TryGetValue(policy, out var admitted) && admitted.Contains(permission);
            foreach (var route in routes.Where(x => x.Name == BeaconApiRoutes.StripSuffix(request.Name)))
            {
                if (Reaches(route, permission) != gatedForThisRole)
                {
                    failures.Add($"{request.Name} ({policy}) via {route}: reached {Reaches(route, permission)}, expected {gatedForThisRole}");
                }
            }
        }

        failures.Should().BeEmpty();
    }

    [TestCaseSource(nameof(Permissions))]
    public async Task AFunctionalRoleAlone_ReachesNoProjectData_NotEvenThroughAMembership(string permission)
    {
        var world = new ProjectAccessWorld();
        world.Project(1);
        world.Project(2, requiresMembership: true);
        world.Source(10, 1);
        world.Source(20, 2);
        world.FunctionalUser(3, "ext-ana", 0, permission);
        world.Member(2, 3, ProjectRole.ProjectAdmin);

        var scope = await ScopeAsync(world, "ext-ana");

        scope.IsUnrestricted.Should().BeFalse();
        foreach (var action in Enum.GetValues<ProjectAction>())
        {
            var set = scope.For(action);
            set.Ids.Should().BeEmpty($"{permission} grants no {action} in any project");
            set.IncludesOpen.Should().BeFalse($"{permission} grants no {action} on the openly reachable data sources");
        }
    }

    [Test]
    public async Task AFunctionalRoleBesideAGlobalRole_AddsNoProjectAccess()
    {
        var world = new ProjectAccessWorld();
        world.Project(1);
        world.Project(2, requiresMembership: true);
        world.Source(10, 1);
        world.Source(20, 2);
        world.FunctionalUser(3, "ext-ana", RoleService.RoleLevels.Viewer, BeaconPermissions.All.ToArray());

        var scope = await ScopeAsync(world, "ext-ana");

        scope.IsUnrestricted.Should().BeFalse("only an Admin bypasses project access");
        scope.Allows(1, ProjectAction.Read).Should().BeTrue("the open project, as a Viewer");
        scope.Allows(1, ProjectAction.Write).Should().BeFalse();
        scope.Allows(2, ProjectAction.Read).Should().BeFalse("the restricted project needs a membership");
    }

    [Test]
    public async Task OverTheApi_AnAuditor_ReadsTheAuditsAndSettings_AndNothingElse()
    {
        await using var app = await StartAsync(BeaconPermissions.Auditor);

        (await SendAsync(app, HttpMethod.Get, "/beacon/api/mcp/audit?from=2026-01-01&to=2026-01-02")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(app, HttpMethod.Get, "/beacon/api/security-audit/?from=2026-01-01&to=2026-01-02")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(app, HttpMethod.Post, "/beacon/api/security-audit/verify", "{\"from\":\"2026-01-01\",\"to\":\"2026-01-02\"}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(app, HttpMethod.Get, "/beacon/api/mcp/settings")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(app, HttpMethod.Get, "/beacon/api/api-keys/admin/")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(app, HttpMethod.Put, "/beacon/api/mcp/settings", "{\"data\":{}}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(app, HttpMethod.Get, "/beacon/api/queries/1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(app, HttpMethod.Get, "/beacon/api/users/")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(app, HttpMethod.Get, "/beacon/api/approvals/changes")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "change requests are read by an Approver, or by a reader of their projects");
    }

    [Test]
    public async Task OverTheApi_AnApprover_ReadsAndDecidesApprovals_AndNothingElse()
    {
        await using var app = await StartAsync(BeaconPermissions.Approver);

        (await SendAsync(app, HttpMethod.Get, "/beacon/api/approvals/pending")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(app, HttpMethod.Get, "/beacon/api/approvals/1")).StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        (await SendAsync(app, HttpMethod.Post, "/beacon/api/approvals/1/approve", "{\"comment\":null}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SendAsync(app, HttpMethod.Get, "/beacon/api/approvals/changes")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(app, HttpMethod.Get, "/beacon/api/approvals/changes/1")).StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        (await SendAsync(app, HttpMethod.Post, "/beacon/api/approvals/changes/1/approve", "{\"comment\":null,\"expectedPayloadHash\":\"x\"}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SendAsync(app, HttpMethod.Post, "/beacon/api/approvals/changes/1/reject", "{\"comment\":null}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await SendAsync(app, HttpMethod.Get, "/beacon/api/queries/1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(app, HttpMethod.Get, "/beacon/api/mcp/audit?from=2026-01-01&to=2026-01-02")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(app, HttpMethod.Get, "/beacon/api/security-audit/?from=2026-01-01&to=2026-01-02")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task OverTheApi_AUserAdmin_ManagesMembersOfAnyProject_ButReadsNoProject()
    {
        await using var app = await StartAsync(BeaconPermissions.UserAdmin);

        (await SendAsync(app, HttpMethod.Get, "/beacon/api/projects/1/members")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(app, HttpMethod.Put, "/beacon/api/projects/1/members/8", "{\"role\":1}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(app, HttpMethod.Put, "/beacon/api/projects/1/access", "{\"requiresMembership\":true}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await SendAsync(app, HttpMethod.Get, "/beacon/api/projects/1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await SendAsync(app, HttpMethod.Post, "/beacon/api/projects/1/data-sources", "{\"dataSourceIds\":[10]}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Test]
    public async Task OverTheApi_AMigrationOperator_ReachesTheMigrationRoutes_OnlyOnAHostThatRunsMigrations()
    {
        await using var running = await StartAsync(BeaconPermissions.MigrationOperator);
        await using var off = await StartAsync(BeaconPermissions.MigrationOperator, migrationsEnabled: false);

        (await SendAsync(running, HttpMethod.Get, "/beacon/api/migrations/jobs")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await (await SendAsync(running, HttpMethod.Get, "/beacon/api/auth/permissions")).Content.ReadAsStringAsync())
            .Should().Contain("\"migrationsEnabled\":true");
        foreach (var (method, path) in new[]
        {
            (HttpMethod.Get, "/beacon/api/migrations/jobs"),
            (HttpMethod.Get, "/beacon/api/migrations/jobs/1"),
            (HttpMethod.Post, "/beacon/api/migrations/jobs"),
            (HttpMethod.Post, "/beacon/api/migrations/jobs/1/run"),
            (HttpMethod.Delete, "/beacon/api/migrations/jobs/1"),
            (HttpMethod.Get, "/beacon/api/migrations/executions")
        })
        {
            (await SendAsync(off, method, path, method == HttpMethod.Post ? "{}" : null)).StatusCode
                .Should().Be(HttpStatusCode.NotFound, $"{method} {path} is not mapped while migrations are off");
        }

        (await (await SendAsync(off, HttpMethod.Get, "/beacon/api/auth/permissions")).Content.ReadAsStringAsync())
            .Should().Contain("\"migrationsEnabled\":false");
    }

    [Test]
    public async Task OverTheApi_ARoleClaimWithoutThePermissionNow_IsRefused()
    {
        // The session still carries the Auditor role claim (the policy passes), but the user no longer holds it.
        await using var app = await StartAsync(BeaconPermissions.Auditor, holds: []);

        var response = await SendAsync(app, HttpMethod.Get, "/beacon/api/mcp/audit?from=2026-01-01&to=2026-01-02");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Auditor permission is required");
    }

    // The route's policies admit the permission (a role claim for it suffices; the Admin policy never does), and the
    // permission filter admits its holder without a Viewer or Editor role.
    private static bool Reaches(BeaconApiRoute route, string permission)
    {
        var policyAdmits = !route.Policies.Contains(BeaconApiEndpoints.AdminPolicyName)
            && route.Policies
                .Where(BeaconPermissions.Policies.ContainsKey)
                .All(x => BeaconPermissions.Policies[x].Contains(permission));

        return policyAdmits && route.AdmittedPermissions.Contains(permission);
    }

    private static async Task<ProjectAccessScope> ScopeAsync(ProjectAccessWorld world, string externalId)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, externalId)], "Cookies"));
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } };
        var services = new ServiceCollection();
        world.Register(services);
        services.AddSingleton<IHttpContextAccessor>(accessor);

        // The read and write permission a stored user's role levels give, as Beacon user management's provider decides.
        var users = new Mock<IUserManagementService>();
        users
            .Setup(x => x.GetUserByExternalIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string id, CancellationToken _) => world.Users
                .Where(x => x.ExternalId == id)
                .Select(x =>
                    new BeaconUserData
                    {
                        Id = x.Id,
                        ExternalId = x.ExternalId,
                        UserName = x.UserName,
                        IsEnabled = x.IsEnabled,
                        Roles = x.UserRoles
                            .Select(y => new BeaconRoleData { Name = y.Role.Name, Level = y.Role.Level })
                            .ToList()
                    })
                .FirstOrDefault());
        services.AddScoped<IBeaconAuthorizationProvider>(x => new DatabaseAuthorizationProvider(accessor, users.Object));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        return await scope.ServiceProvider.GetRequiredService<IProjectAccessEvaluator>().GetScopeAsync(CancellationToken.None);
    }

    // MapBeaconApi over a test server, for a signed-in user with the role claim of <paramref name="permission"/> and no
    // Viewer or Editor role, who holds <paramref name="holds"/> now (default: that permission), on a host that runs data
    // migrations unless <paramref name="migrationsEnabled"/> is false.
    private static async Task<WebApplication> StartAsync(string permission, string[]? holds = null, bool migrationsEnabled = true)
    {
        var configuration = new BeaconConfiguration();
        configuration.UserManagement.Enabled = true;
        var authorization = new Mock<IBeaconAuthorizationProvider>();
        authorization.Setup(x => x.HasReadPermissionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        authorization.Setup(x => x.HasWritePermissionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(false);
        authorization.Setup(x => x.AllowsFunctionalPermissionAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(configuration);
        builder.Services.AddSingleton(new BeaconApiOptions { Realtime = false });
        builder.Services.AddSingleton(Options.Create(new MigrationOptions { Enabled = migrationsEnabled }));
        builder.Services.AddSingleton(Mock.Of<IMediator>());
        builder.Services.AddSingleton(authorization.Object);
        builder.Services.AddSingleton(TestPermissions.Holding(holds ?? [permission]));
        builder.Services.AddSingleton(Mock.Of<IBeaconUserContext>());
        builder.Services.AddSingleton(Mock.Of<IActorUserResolver>());
        builder.Services.AddSingleton(Mock.Of<IDbContextFactory<BeaconContext>>());
        builder.Services.AddAntiforgery(x =>
        {
            x.Cookie.Name = AntiforgeryCookieName;
            x.HeaderName = "X-XSRF-TOKEN";
        });
        builder.Services.AddBeaconApiAuthorization();
        builder.Services
            .AddAuthentication("Test")
            .AddCookie("Test", x => x.Events.OnRedirectToAccessDenied = y =>
            {
                y.Response.StatusCode = StatusCodes.Status403Forbidden;
                return Task.CompletedTask;
            });

        var app = builder.Build();
        var user = User(permission);
        app.Use((context, next) =>
        {
            if (context.Request.Headers.ContainsKey(TestUserHeader))
            {
                context.User = user;
            }

            return next(context);
        });
        app.UseAuthorization();
        app.MapBeaconApi();
        await app.StartAsync();

        return app;
    }

    private static ClaimsPrincipal User(string permission)
    {
        return new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "ext-ana"), new Claim(ClaimTypes.Role, permission)],
            "Cookies"));
    }

    // Authenticated request with a valid antiforgery pair, minted for the same user.
    private static Task<HttpResponseMessage> SendAsync(WebApplication app, HttpMethod method, string path, string? json = null)
    {
        // Antiforgery tokens are bound to the user's NameIdentifier, which every test user shares.
        var tokens = app.Services
            .GetRequiredService<IAntiforgery>()
            .GetAndStoreTokens(new DefaultHttpContext { RequestServices = app.Services, User = User(BeaconPermissions.Auditor) });

        var request = new HttpRequestMessage(method, path);
        request.Headers.Add(TestUserHeader, "1");
        request.Headers.Add("Cookie", $"{AntiforgeryCookieName}={tokens.CookieToken}");
        request.Headers.Add("X-XSRF-TOKEN", tokens.RequestToken);
        if (json != null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        return app.GetTestClient().SendAsync(request);
    }
}
