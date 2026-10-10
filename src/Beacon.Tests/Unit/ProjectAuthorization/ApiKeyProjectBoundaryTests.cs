using System.Net;
using System.Net.Http.Json;
using Beacon.Api;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.Authorization;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Data.Entities.Projects;
using Beacon.Core.Data.Enums;
using Beacon.Core.Services;
using Beacon.Core.Services.Security;
using Beacon.Core.Worker;
using Beacon.Tests.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// An API key restricted to project 1 is confined to it over REST, end to end: the principal the API key middleware
/// builds from the stored key, the real actor accessor and project access evaluator, the real resource resolver and
/// MediatR pipeline, over laid-out rows (in-memory sets, never a database). Project 2 is open, and still out of the
/// key's reach; project 3 is restricted.
/// </summary>
[TestFixture]
public class ApiKeyProjectBoundaryTests
{
    private const string Key = "sk-sem_confined0123456789";

    [Test]
    public async Task ARestrictedKey_ReadsADataSourceOfItsProject()
    {
        await using var app = await StartAsync();

        var response = await SendAsync(app, "/beacon/api/data-sources/10");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<DataSourceBody>())!.Name.Should().Be("source 10");
    }

    [TestCase("/beacon/api/data-sources/20", TestName = "ARestrictedKey_IsConfinedOverRest_EvenAwayFromAnOpenProject")]
    [TestCase("/beacon/api/data-sources/30", TestName = "ARestrictedKey_IsConfinedOverRest_AwayFromARestrictedProject")]
    [TestCase("/beacon/api/projects/2", TestName = "ARestrictedKey_DoesNotReadAnotherProject")]
    public async Task ARestrictedKey_IsConfinedToItsProjects(string path)
    {
        await using var app = await StartAsync();

        var response = await SendAsync(app, path);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Test]
    public async Task ARestrictedKey_ListsOnlyItsProjects()
    {
        await using var app = await StartAsync();

        var response = await SendAsync(app, "/beacon/api/projects");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<PageBody>();
        page!.Items.Select(x => x.Id).Should().Equal(1);
        page.TotalCount.Should().Be(1);
    }

    private static async Task<HttpResponseMessage> SendAsync(WebApplication app, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("Authorization", $"Bearer {Key}");

        return await app.GetTestClient().SendAsync(request);
    }

    private static async Task<WebApplication> StartAsync()
    {
        var owner = new BeaconUser
        {
            Id = 7,
            UserName = "ada",
            ExternalId = "ext-ada",
            IsEnabled = true,
            UserRoles = [new BeaconUserRole { UserId = 7, Role = new BeaconRole { Name = "Viewer", Level = 1 } }]
        };
        var apiKeys = new Mock<IApiKeyService>();
        apiKeys
            .Setup(x => x.ValidateApiKeyAsync(Key, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ApiKeyCredential
            {
                Id = 41,
                UserId = owner.Id,
                Name = "integration",
                KeyHash = "hash",
                KeyPrefix = Key[..12],
                Scopes = "[\"Read\"]",
                AllowedProjectIds = "[1]",
                User = owner
            });

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        // A throwaway key: AddBeaconServices refuses to start without one (§1.1). Never a real secret (§1.2).
        builder.Configuration["Beacon:EncryptionKey"] = Convert.ToBase64String(new byte[32]);
        builder.Services.AddBeaconServices(builder.Configuration, x => x.AddBeaconScheduler<NoOpScheduler>());
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<IBeaconUserContext, HttpContextUserContext>();
        builder.Services.AddSingleton(Rows(owner));
        builder.Services.AddSingleton(apiKeys.Object);

        // Endpoints this test does not call bind these; they are never asked.
        builder.Services.TryAddSingleton(Mock.Of<IActorUserResolver>());
        builder.Services.TryAddSingleton(Mock.Of<IRoleService>());
        builder.Services.TryAddSingleton(Mock.Of<IUserManagementService>());
        builder.Services.AddSingleton(new BeaconApiOptions { Realtime = false });
        builder.Services.AddRouting();
        builder.Services.AddAntiforgery();
        builder.Services.AddBeaconApiAuthorization();
        var app = builder.Build();

        app.UseMiddleware<ApiKeyAuthMiddleware>();
        app.UseRouting();
        app.UseAuthorization();
        app.MapBeaconApi();
        await app.StartAsync();

        return app;
    }

    // Projects 1 and 2 are open, 3 restricted; each links its own data source (10, 20, 30). The key's owner is a Viewer.
    private static IDbContextFactory<BeaconContext> Rows(BeaconUser owner)
    {
        var projects = new List<Project>
        {
            new() { Id = 1, Name = "Allowed" },
            new() { Id = 2, Name = "Open elsewhere" },
            new() { Id = 3, Name = "Restricted", RequiresMembership = true }
        };
        var sources = new List<DataSource>();
        var links = new List<ProjectDataSource>();
        foreach (var project in projects)
        {
            var source = new DataSource { Id = project.Id * 10, Name = $"source {project.Id * 10}", DataSourceType = DataSourceType.Database, EncryptedConnectionData = "enc" };
            var link = new ProjectDataSource { ProjectId = project.Id, Project = project, DataSourceId = source.Id, DataSource = source };
            sources.Add(source);
            links.Add(link);
            project.DataSources.Add(link);
        }

        var users = new List<BeaconUser> { owner };
        var saved = new List<object>();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new RecordingBeaconContext(
                new Dictionary<Type, object>
                {
                    [typeof(Project)] = RecordingBeaconContext.MemorySet(projects, saved),
                    [typeof(DataSource)] = RecordingBeaconContext.MemorySet(sources, saved),
                    [typeof(ProjectDataSource)] = RecordingBeaconContext.MemorySet(links, saved),
                    [typeof(ProjectMember)] = RecordingBeaconContext.MemorySet(new List<ProjectMember>(), saved),
                    [typeof(BeaconUser)] = RecordingBeaconContext.MemorySet(users, saved)
                },
                saved,
                new SqlCapture()));

        return factory.Object;
    }

    private sealed record DataSourceBody(int Id, string Name);

    private sealed record PageBody(List<ProjectBody> Items, int TotalCount);

    private sealed record ProjectBody(int Id, string Name);

    private sealed class NoOpScheduler : IBeaconScheduler
    {
        public Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron) => Task.CompletedTask;

        public Task Remove(int subscriptionId, string subscriptionName) => Task.CompletedTask;
    }
}
