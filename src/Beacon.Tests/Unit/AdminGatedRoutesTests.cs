using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using NUnit.Framework;
using Beacon.Api.Endpoints;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Handlers.Home;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Services;
using Beacon.Tests.Common;
using Beacon.Core.Models;

namespace Beacon.Tests.Unit;

/// <summary>
/// The Admin role's duties, split into functional permissions: every privileged route requires exactly the permission
/// policy of its duty (and admits its holder in the permission filter, with no Viewer or Editor role), the duties that
/// stay an Admin's keep the Admin policy, and the routes a permission also opens (approval reads, project membership,
/// the Home migration summary) admit its holder beside everyone else. Each policy admits the Admin role and the role its
/// permission is mapped to, nothing else. The home trends window is bounded.
/// </summary>
[TestFixture]
public class AdminGatedRoutesTests
{
    /// <summary>Every privileged route and the permission policy it requires.</summary>
    internal static readonly IReadOnlyDictionary<string, string> PermissionPolicyByRoute = new Dictionary<string, string>
    {
        ["POST /beacon/api/approvals/{id:int}/approve"] = BeaconPermissions.ApproverPolicy,
        ["POST /beacon/api/approvals/{id:int}/reject"] = BeaconPermissions.ApproverPolicy,

        ["GET /beacon/api/mcp/audit"] = BeaconPermissions.AuditorPolicy,
        ["GET /beacon/api/mcp/settings"] = BeaconPermissions.AuditorOrSecurityAdminPolicy,
        ["GET /beacon/api/mcp/projects/{projectId:int}/settings"] = BeaconPermissions.AuditorOrSecurityAdminPolicy,
        ["GET /beacon/api/admin-settings/"] = BeaconPermissions.AuditorOrSecurityAdminPolicy,
        ["GET /beacon/api/api-keys/admin/"] = BeaconPermissions.AuditorOrUserAdminPolicy,

        ["PUT /beacon/api/mcp/settings"] = BeaconPermissions.SecurityAdminPolicy,
        ["PUT /beacon/api/mcp/projects/{projectId:int}/settings"] = BeaconPermissions.SecurityAdminPolicy,
        ["PUT /beacon/api/admin-settings/"] = BeaconPermissions.SecurityAdminPolicy,
        ["POST /beacon/api/admin-settings/test-llm"] = BeaconPermissions.SecurityAdminPolicy,
        ["POST /beacon/api/recipients/"] = BeaconPermissions.SecurityAdminPolicy,
        ["PUT /beacon/api/recipients/{id:int}"] = BeaconPermissions.SecurityAdminPolicy,
        ["DELETE /beacon/api/recipients/{id:int}"] = BeaconPermissions.SecurityAdminPolicy,
        ["PUT /beacon/api/queries/{id:int}/mcp-tool"] = BeaconPermissions.SecurityAdminPolicy,
        ["PUT /beacon/api/projects/repositories/{id:int}/token"] = BeaconPermissions.SecurityAdminPolicy,

        ["GET /beacon/api/users/"] = BeaconPermissions.UserAdminPolicy,
        ["GET /beacon/api/users/roles"] = BeaconPermissions.UserAdminPolicy,
        ["POST /beacon/api/users/internal"] = BeaconPermissions.UserAdminPolicy,
        ["POST /beacon/api/users/external"] = BeaconPermissions.UserAdminPolicy,
        ["PUT /beacon/api/users/{id:int}"] = BeaconPermissions.UserAdminPolicy,
        ["POST /beacon/api/users/{id:int}/toggle-enabled"] = BeaconPermissions.UserAdminPolicy,
        ["PUT /beacon/api/users/{id:int}/roles"] = BeaconPermissions.UserAdminPolicy,
        ["DELETE /beacon/api/api-keys/admin/{id:int}"] = BeaconPermissions.UserAdminPolicy,
        ["PUT /beacon/api/subscriptions/{id:int}/owner"] = BeaconPermissions.UserAdminPolicy,
        ["PUT /beacon/api/data-quality/contracts/{id:int}/owner"] = BeaconPermissions.UserAdminPolicy,
        ["PUT /beacon/api/ai-actors/{id:int}/owner"] = BeaconPermissions.UserAdminPolicy,
        ["PUT /beacon/api/projects/{id:int}/access"] = BeaconPermissions.UserAdminPolicy,

        ["POST /beacon/api/data-sources/"] = BeaconPermissions.DataSourceAdminPolicy,
        ["POST /beacon/api/data-sources/test-connection"] = BeaconPermissions.DataSourceAdminPolicy,
        ["DELETE /beacon/api/data-sources/{id:int}"] = BeaconPermissions.DataSourceAdminPolicy,

        ["GET /beacon/api/migrations/jobs"] = BeaconPermissions.MigrationOperatorPolicy,
        ["GET /beacon/api/migrations/jobs/{id:int}"] = BeaconPermissions.MigrationOperatorPolicy,
        ["POST /beacon/api/migrations/jobs"] = BeaconPermissions.MigrationOperatorPolicy,
        ["POST /beacon/api/migrations/jobs/{id:int}/run"] = BeaconPermissions.MigrationOperatorPolicy,
        ["DELETE /beacon/api/migrations/jobs/{id:int}"] = BeaconPermissions.MigrationOperatorPolicy,
        ["GET /beacon/api/migrations/executions"] = BeaconPermissions.MigrationOperatorPolicy
    };

    /// <summary>Routes a functional permission opens besides the usual Viewer/Editor access (no policy of their own).</summary>
    internal static readonly IReadOnlyDictionary<string, string> AdmittingRoutes = new Dictionary<string, string>
    {
        ["GET /beacon/api/approvals/pending"] = BeaconPermissions.Approver,
        ["GET /beacon/api/approvals/{id:int}"] = BeaconPermissions.Approver,
        ["GET /beacon/api/projects/{projectId:int}/members"] = BeaconPermissions.UserAdmin,
        ["GET /beacon/api/projects/{id:int}/member-candidates"] = BeaconPermissions.UserAdmin,
        ["PUT /beacon/api/projects/{id:int}/members/{userId:int}"] = BeaconPermissions.UserAdmin,
        ["DELETE /beacon/api/projects/{id:int}/members/{userId:int}"] = BeaconPermissions.UserAdmin,
        ["GET /beacon/api/home/migration-summary"] = BeaconPermissions.MigrationOperator
    };

    [Test]
    public async Task EveryPrivilegedRoute_RequiresItsPermissionPolicy()
    {
        var routes = (await BeaconApiRoutes.ReadAsync()).ToDictionary(x => x.Key);
        var failures = new List<string>();

        foreach (var (key, policy) in PermissionPolicyByRoute)
        {
            if (!routes.TryGetValue(key, out var route))
            {
                failures.Add($"{key}: not mapped");
                continue;
            }

            var functional = route.Policies
                .Where(BeaconPermissions.Policies.ContainsKey)
                .ToList();
            if (functional.Count != 1 || functional[0] != policy)
            {
                failures.Add($"{key}: carries [{string.Join(", ", functional)}], expected {policy}");
            }

            if (route.Policies.Contains(BeaconApiEndpoints.AdminPolicyName))
            {
                failures.Add($"{key}: still carries the Admin policy");
            }

            if (!route.PermissionRequired || !route.AdmittedPermissions.SequenceEqual(BeaconPermissions.Policies[policy]))
            {
                failures.Add($"{key}: the permission filter does not require [{string.Join(", ", BeaconPermissions.Policies[policy])}]");
            }
        }

        failures.Should().BeEmpty();
    }

    [Test]
    public async Task NoOtherRoute_CarriesAFunctionalPermissionPolicy()
    {
        var routes = await BeaconApiRoutes.ReadAsync();

        routes
            .Where(x => x.Policies.Any(BeaconPermissions.Policies.ContainsKey) || x.PermissionRequired)
            .Select(x => x.Key)
            .Should()
            .BeEquivalentTo(PermissionPolicyByRoute.Keys);
    }

    [Test]
    public async Task RoutesAPermissionAlsoOpens_AdmitItsHolder_WithoutAPolicyOfTheirOwn()
    {
        var routes = await BeaconApiRoutes.ReadAsync();

        var admitting = routes
            .Where(x => x.AdmittedPermissions.Count > 0)
            .Where(x => !x.PermissionRequired)
            .ToDictionary(x => x.Key, x => string.Join("|", x.AdmittedPermissions));

        admitting.Should().BeEquivalentTo(AdmittingRoutes);
        routes
            .Where(x => AdmittingRoutes.ContainsKey(x.Key))
            .SelectMany(x => x.Policies)
            .Should()
            .OnlyContain(x => x == BeaconApiEndpoints.AuthPolicyName || x == BeaconApiEndpoints.ExecuteScopePolicyName);
    }

    [Test]
    public async Task CurationLlmAndUnlinking_StayAnAdmins()
    {
        var routes = await BeaconApiRoutes.ReadAsync();

        var adminOnly = routes
            .Where(x => x.Pattern.StartsWith("/beacon/api/glossary", StringComparison.Ordinal)
                || (x.Pattern.StartsWith("/beacon/api/eval", StringComparison.Ordinal) && x.Name != "RecordQueryFeedback")
                || x.Name == "UnlinkProjectDataSource")
            .ToList();

        adminOnly.Should().HaveCountGreaterThan(8);
        adminOnly.Should().OnlyContain(x => x.Policies.Contains(BeaconApiEndpoints.AdminPolicyName), "curation and unlinking stay an Admin's");
        adminOnly.Should().OnlyContain(x => x.AdmittedPermissions.Count == 0);
        adminOnly
            .Where(x => x.Name != "UnlinkProjectDataSource")
            .Should()
            .OnlyContain(x => x.RequiresLiveAdmin, "curation needs an Admin by live roles, not a session's stale Admin claim");
    }

    [Test]
    public async Task OnAHostWithoutUserManagement_APermissionPolicy_AdmitsTheHostsRoleClaimsOnlyOnOptIn()
    {
        var withoutUserManagement = new Beacon.Core.BeaconConfiguration();
        withoutUserManagement.UserManagement.Enabled = false;
        await using var notOptedIn = AuthorizationServices(new RoleMappingOptions(), withoutUserManagement);
        await using var optedIn = AuthorizationServices(new RoleMappingOptions { Roles = new FunctionalRoleNames { HonourHostRoleClaims = true } }, withoutUserManagement);

        var refused = await notOptedIn.GetRequiredService<IAuthorizationService>().AuthorizeAsync(Principal(BeaconPermissions.Auditor), resource: null, BeaconPermissions.AuditorPolicy);
        var admin = await notOptedIn.GetRequiredService<IAuthorizationService>().AuthorizeAsync(Principal(RoleService.RoleNames.Admin), resource: null, BeaconPermissions.AuditorPolicy);
        var admitted = await optedIn.GetRequiredService<IAuthorizationService>().AuthorizeAsync(Principal(BeaconPermissions.Auditor), resource: null, BeaconPermissions.AuditorPolicy);

        refused.Succeeded.Should().BeFalse("a host's role claims grant a permission only with Roles:HonourHostRoleClaims");
        admin.Succeeded.Should().BeTrue();
        admitted.Succeeded.Should().BeTrue();
    }

    [Test]
    public async Task APermissionPolicy_ComparesTheMappedRoleNameIgnoringCase()
    {
        await using var provider = AuthorizationServices(new RoleMappingOptions());

        var result = await provider.GetRequiredService<IAuthorizationService>().AuthorizeAsync(Principal("auditor"), resource: null, BeaconPermissions.AuditorPolicy);

        result.Succeeded.Should().BeTrue("role names are compared as the permission accessor compares them");
    }

    [TestCase(BeaconPermissions.ApproverPolicy, new[] { BeaconPermissions.Approver })]
    [TestCase(BeaconPermissions.AuditorPolicy, new[] { BeaconPermissions.Auditor })]
    [TestCase(BeaconPermissions.SecurityAdminPolicy, new[] { BeaconPermissions.SecurityAdmin })]
    [TestCase(BeaconPermissions.UserAdminPolicy, new[] { BeaconPermissions.UserAdmin })]
    [TestCase(BeaconPermissions.DataSourceAdminPolicy, new[] { BeaconPermissions.DataSourceAdmin })]
    [TestCase(BeaconPermissions.MigrationOperatorPolicy, new[] { BeaconPermissions.MigrationOperator })]
    [TestCase(BeaconPermissions.AuditorOrSecurityAdminPolicy, new[] { BeaconPermissions.Auditor, BeaconPermissions.SecurityAdmin })]
    [TestCase(BeaconPermissions.AuditorOrUserAdminPolicy, new[] { BeaconPermissions.Auditor, BeaconPermissions.UserAdmin })]
    public async Task EachPermissionPolicy_AdmitsTheAdminAndItsRoles_AndNoOtherRole(string policy, string[] admitted)
    {
        await using var provider = AuthorizationServices(new RoleMappingOptions());
        var authorization = provider.GetRequiredService<IAuthorizationService>();

        foreach (var role in BeaconPermissions.All.Append(RoleService.RoleNames.Admin).Append(RoleService.RoleNames.Editor).Append(RoleService.RoleNames.Viewer))
        {
            var result = await authorization.AuthorizeAsync(Principal(role), resource: null, policy);
            var expected = role == RoleService.RoleNames.Admin || admitted.Contains(role);
            result.Succeeded.Should().Be(expected, $"{policy} {(expected ? "admits" : "refuses")} {role}");
        }

        var apiKey = new ClaimsPrincipal(new ClaimsIdentity([new Claim("auth_method", "api_key"), new Claim("scope", "Execute")], "ApiKey"));
        (await authorization.AuthorizeAsync(apiKey, resource: null, policy)).Succeeded.Should().BeFalse("an API key carries no role");
    }

    [Test]
    public async Task APermissionPolicy_AdmitsTheConfiguredRoleName_InsteadOfTheDefault()
    {
        var mapping = new RoleMappingOptions { Roles = new FunctionalRoleNames { Auditor = "Compliance Reviewer" } };
        await using var provider = AuthorizationServices(mapping);
        var authorization = provider.GetRequiredService<IAuthorizationService>();

        (await authorization.AuthorizeAsync(Principal("Compliance Reviewer"), resource: null, BeaconPermissions.AuditorPolicy)).Succeeded.Should().BeTrue();
        (await authorization.AuthorizeAsync(Principal(BeaconPermissions.Auditor), resource: null, BeaconPermissions.AuditorPolicy)).Succeeded.Should().BeFalse();
        (await authorization.AuthorizeAsync(Principal("Compliance Reviewer"), resource: null, BeaconPermissions.UserAdminPolicy)).Succeeded.Should().BeFalse();
    }

    [Test]
    public async Task HomeMigrationSummary_StaysReadableByEveryUser()
    {
        var policies = await PoliciesByRouteAsync();

        policies.Should().ContainKey("GET /beacon/api/home/migration-summary");
        policies["GET /beacon/api/home/migration-summary"].Should().NotContain(BeaconApiEndpoints.AdminPolicyName);
        policies["GET /beacon/api/home/migration-summary"].Should().NotContain(BeaconPermissions.MigrationOperatorPolicy);
    }

    [TestCase(int.MinValue, 1)]
    [TestCase(-5, 1)]
    [TestCase(0, 1)]
    [TestCase(1, 1)]
    [TestCase(30, 30)]
    [TestCase(89, 89)]
    [TestCase(90, 90)]
    [TestCase(91, 90)]
    [TestCase(100000, 90)]
    [TestCase(int.MaxValue, 90)]
    public void HomeTrends_WindowIsClampedToOneToNinetyDays(int requested, int window)
    {
        new GetHomeTrendsQuery(requested).WindowDays.Should().Be(window);
    }

    [TestCase(100000, 90)]
    [TestCase(91, 90)]
    [TestCase(-5, 1)]
    [TestCase(int.MinValue, 1)]
    public async Task HomeTrends_TheHandlerReadsOnlyTheClampedWindow(int requested, int window)
    {
        // The subscription count, then the count as of the window's start, whose cutoff is the clamped window.
        var capture = new SqlCapture().ThenScalar(0);
        var handler = new GetHomeTrendsHandler(capture.Factory(), TestProjectAccess.Evaluator(TestProjectAccess.Unrestricted));

        var act = () => handler.Handle(new GetHomeTrendsQuery(requested), CancellationToken.None);

        await act.Should().ThrowAsync<SqlCapturedException>();
        capture.Commands[1].Should().Contain("created_time <= @");
        capture.CommandParameters[1].Values.OfType<DateTime>().Should().ContainSingle()
            .Which.Should().BeCloseTo(DateTime.UtcNow.AddDays(-window), TimeSpan.FromMinutes(1));
    }

    [Test]
    public async Task CreateProject_WithAnAccessTokenByANonAdmin_IsForbiddenAndCreatesNothing()
    {
        var saved = new List<object>();
        var logs = new LogRecorder();
        var handler = CreateProjectHandler(saved, Principal(RoleService.RoleNames.Editor), logs);

        var act = () => handler.Handle(new CreateProjectCommand("p", null, [], ["https://github.com/acme/repo"], "ghp_secret"), CancellationToken.None);

        await act.Should().ThrowAsync<BeaconForbiddenException>();
        saved.Should().BeEmpty();
        var entry = logs.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("ext-1").And.NotContain("ghp_secret");
    }

    [Test]
    public async Task CreateProject_WithAnAccessToken_IsASecurityAdminsToo()
    {
        var saved = new List<object>();

        await CreateProjectHandler(saved, Principal(BeaconPermissions.SecurityAdmin))
            .Handle(new CreateProjectCommand("p", null, [], ["https://github.com/acme/repo"], "ghp_secret"), CancellationToken.None);

        saved.OfType<Project>().Should().ContainSingle().Which.Repositories.Should().OnlyContain(x => x.EncryptedAccessToken == "enc");
    }

    [Test]
    public async Task CreateProject_WithoutAnAccessToken_IsAnyWritersAndWithOne_AnAdmins()
    {
        var editorSaved = new List<object>();
        var adminSaved = new List<object>();

        await CreateProjectHandler(editorSaved, Principal(RoleService.RoleNames.Editor))
            .Handle(new CreateProjectCommand("p", null, [], ["https://github.com/acme/repo"]), CancellationToken.None);
        await CreateProjectHandler(adminSaved, Principal(RoleService.RoleNames.Admin))
            .Handle(new CreateProjectCommand("p", null, [], ["https://github.com/acme/repo"], "ghp_secret"), CancellationToken.None);

        editorSaved.OfType<Project>().Should().ContainSingle().Which.Repositories.Should().OnlyContain(x => x.EncryptedAccessToken == null);
        adminSaved.OfType<Project>().Should().ContainSingle().Which.Repositories.Should().OnlyContain(x => x.EncryptedAccessToken == "enc");
    }

    private static CreateProjectHandler CreateProjectHandler(List<object> saved, ClaimsPrincipal caller, LogRecorder? logs = null)
    {
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object> { [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(new List<BeaconUser>(), []) },
                saved));
        var encryption = new Mock<IEncryptionService>();
        encryption.Setup(x => x.Encrypt(It.IsAny<string>())).Returns("enc");
        var accessor = new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = caller } };

        return new CreateProjectHandler(
            factory.Object,
            encryption.Object,
            TestProjectAccess.ActorAccessor(accessor, factory.Object),
            TestPermissions.ForPrincipal(caller),
            TestProjectAccess.Evaluator(TestProjectAccess.Unrestricted),
            Mock.Of<IProjectMembershipSource>(x => x.IsAvailable == false),
            Options.Create(new ProjectAuthorizationOptions()),
            (logs ?? new LogRecorder()).For<CreateProjectHandler>());
    }

    private static ClaimsPrincipal Principal(string role)
    {
        Claim[] claims = [new(ClaimTypes.NameIdentifier, "ext-1"), new(ClaimTypes.Role, role)];

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));
    }

    private static ServiceProvider AuthorizationServices(RoleMappingOptions mapping, Beacon.Core.BeaconConfiguration? configuration = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Options.Create(mapping));
        if (configuration != null)
        {
            services.AddSingleton(configuration);
        }

        services.AddBeaconApiAuthorization();

        return services.BuildServiceProvider();
    }

    private static async Task<Dictionary<string, List<string?>>> PoliciesByRouteAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(Mock.Of<IMediator>());
        builder.Services.AddSingleton(Mock.Of<IActorUserResolver>());
        builder.Services.AddRouting();
        await using var app = builder.Build();

        var api = app.MapGroup("/beacon/api");
        api.MapUsersEndpoints();
        api.MapMcpManagementEndpoints();
        api.MapMigrationsEndpoints();
        api.MapProjectsEndpoints();
        api.MapHomeEndpoints();
        api.MapDataQualityEndpoints();
        api.MapAiActorsEndpoints();

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .SelectMany(x => (x.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? [])
                .Select(y =>
                    new
                    {
                        Route = $"{y} {x.RoutePattern.RawText}",
                        Policies = x.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(z => z.Policy).ToList()
                    }))
            .GroupBy(x => x.Route)
            .ToDictionary(x => x.Key, x => x.SelectMany(y => y.Policies).ToList());
    }
}
