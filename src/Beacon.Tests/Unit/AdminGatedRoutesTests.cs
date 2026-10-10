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
using Moq;
using NUnit.Framework;
using Beacon.Api.Endpoints;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Handlers.Home;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// Reads that disclose the user directory, the MCP security settings or the data-migration catalogue, every way of
/// setting a repository access token, and changing who owns a data contract or AI actor, are an Admin's. The home trends
/// window is bounded.
/// </summary>
[TestFixture]
public class AdminGatedRoutesTests
{
    private static readonly string[] AdminOnlyRoutes =
    [
        "GET /beacon/api/users/",
        "GET /beacon/api/users/roles",
        "GET /beacon/api/mcp/settings",
        "GET /beacon/api/mcp/projects/{projectId:int}/settings",
        "GET /beacon/api/migrations/jobs",
        "GET /beacon/api/migrations/jobs/{id:int}",
        "GET /beacon/api/migrations/executions",
        "PUT /beacon/api/projects/repositories/{id:int}/token",
        "PUT /beacon/api/data-quality/contracts/{id:int}/owner",
        "PUT /beacon/api/ai-actors/{id:int}/owner"
    ];

    [Test]
    public async Task AdminOnlyRoutes_RequireTheAdminPolicy()
    {
        var policies = await PoliciesByRouteAsync();

        foreach (var route in AdminOnlyRoutes)
        {
            policies.Should().ContainKey(route);
            policies[route].Should().Contain(BeaconApiEndpoints.AdminPolicyName, route);
        }
    }

    [Test]
    public async Task HomeMigrationSummary_StaysReadableByEveryUser()
    {
        var policies = await PoliciesByRouteAsync();

        policies.Should().ContainKey("GET /beacon/api/home/migration-summary");
        policies["GET /beacon/api/home/migration-summary"].Should().NotContain(BeaconApiEndpoints.AdminPolicyName);
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
        var handler = new GetHomeTrendsHandler(capture.Factory());

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

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        saved.Should().BeEmpty();
        var entry = logs.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain("ext-1").And.NotContain("ghp_secret");
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
            new BeaconActorAccessor(accessor, factory.Object),
            (logs ?? new LogRecorder()).For<CreateProjectHandler>());
    }

    private static ClaimsPrincipal Principal(string role)
    {
        Claim[] claims = [new(ClaimTypes.NameIdentifier, "ext-1"), new(ClaimTypes.Role, role)];

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Cookies"));
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
