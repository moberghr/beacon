using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Beacon.Api.Endpoints;
using Beacon.Core.Authorization;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.Handlers.Projects.Members;
using Beacon.Core.Helpers;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// The membership and link routes bind the project, user and body into their one request each (§2.1.1).
/// </summary>
[TestFixture]
public class ProjectMembershipEndpointsTests
{
    [Test]
    public async Task EachRoute_SendsItsRequest_WithTheRouteAndBodyValues()
    {
        var sent = new List<object>();
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<object, CancellationToken>((x, _) => sent.Add(x))
            .ReturnsAsync((object?)null);
        mediator
            .Setup(x => x.Send(It.IsAny<IRequest<PagedList<ProjectMemberEntry>>>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<PagedList<ProjectMemberEntry>>, CancellationToken>((x, _) => sent.Add(x))
            .ReturnsAsync(PagedList<ProjectMemberEntry>.Create([], 0, 20));
        mediator
            .Setup(x => x.Send(It.IsAny<IRequest<SearchProjectMemberCandidatesResult>>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<SearchProjectMemberCandidatesResult>, CancellationToken>((x, _) => sent.Add(x))
            .ReturnsAsync(new SearchProjectMemberCandidatesResult([]));
        mediator
            .Setup(x => x.Send(It.IsAny<IRequest<SetProjectMemberResult>>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<SetProjectMemberResult>, CancellationToken>((x, _) => sent.Add(x))
            .ReturnsAsync(new SetProjectMemberResult(5, 8, ProjectRole.Editor, true));
        mediator
            .Setup(x => x.Send(It.IsAny<IRequest>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest, CancellationToken>((x, _) => sent.Add(x))
            .Returns(Task.CompletedTask);
        mediator
            .Setup(x => x.Send(It.IsAny<IRequest<SetProjectAccessModeResult>>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<SetProjectAccessModeResult>, CancellationToken>((x, _) => sent.Add(x))
            .ReturnsAsync(new SetProjectAccessModeResult(5, true, []));
        mediator
            .Setup(x => x.Send(It.IsAny<IRequest<LinkProjectDataSourcesResult>>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<LinkProjectDataSourcesResult>, CancellationToken>((x, _) => sent.Add(x))
            .ReturnsAsync(new LinkProjectDataSourcesResult(5, [10]));
        await using var app = await StartAsync(mediator.Object);
        var client = app.GetTestClient();

        (await client.GetAsync("/beacon/api/projects/5/members?page=1&pageSize=10")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/beacon/api/projects/5/member-candidates?search=an")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PutAsJsonAsync("/beacon/api/projects/5/members/8", new { role = 2 })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.DeleteAsync("/beacon/api/projects/5/members/8")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await client.PutAsJsonAsync("/beacon/api/projects/5/access", new { requiresMembership = true })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync("/beacon/api/projects/5/data-sources", new { dataSourceIds = new[] { 10 } })).StatusCode.Should().Be(HttpStatusCode.OK);

        sent.Should().BeEquivalentTo(
            new object[]
            {
                new GetProjectMembersQuery { ProjectId = 5, Page = 1, PageSize = 10 },
                new SearchProjectMemberCandidatesQuery(5, "an"),
                new SetProjectMemberCommand(5, 8, ProjectRole.Editor),
                new RemoveProjectMemberCommand(5, 8),
                new SetProjectAccessModeCommand(5, true),
                new LinkProjectDataSourcesCommand(5, [10])
            },
            x => x.WithStrictOrdering().RespectingRuntimeTypes());
    }

    private static async Task<WebApplication> StartAsync(IMediator mediator)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(mediator);
        builder.Services.AddRouting();
        builder.Services.AddAuthorization(x =>
        {
            x.AddPolicy(BeaconApiEndpoints.AdminPolicyName, y => y.RequireAssertion(_ => true));
            foreach (var policy in BeaconPermissions.Policies.Keys)
            {
                x.AddPolicy(policy, y => y.RequireAssertion(_ => true));
            }
        });
        var app = builder.Build();
        app.UseAuthorization();
        app.MapGroup("/beacon/api").MapProjectsEndpoints();
        await app.StartAsync();

        return app;
    }
}
