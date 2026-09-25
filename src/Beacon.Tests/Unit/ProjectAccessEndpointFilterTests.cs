using System.Net;
using System.Security.Claims;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;
using Beacon.Api.Endpoints;
using Beacon.Core.Handlers.Projects;
using Beacon.Core.HostDocs;

namespace Beacon.Tests.Unit;

/// <summary>
/// §1.4 on REST: a project-restricted API key (or MCP JWT caller) must not read another project's imported documents
/// through <c>/projects/{id}/imported-documents</c>.
/// </summary>
[TestFixture]
public class ProjectAccessEndpointFilterTests
{
    [Test]
    public void IsAllowed_FollowsTheAllowedProjectsClaim_AndFailsClosed()
    {
        ProjectAccess.IsAllowed(Principal(("auth_method", "api_key"), ("allowed_projects", "[1,2]")), 2).Should().BeTrue();
        ProjectAccess.IsAllowed(Principal(("auth_method", "api_key"), ("allowed_projects", "[1,2]")), 3).Should().BeFalse();
        ProjectAccess.IsAllowed(Principal(("auth_method", "api_key"), ("allowed_projects", "not-json")), 1).Should().BeFalse("a malformed restriction denies");
        ProjectAccess.IsAllowed(Principal(("auth_method", "api_key")), 1).Should().BeFalse("a scoped caller without the claim is denied, as on MCP");
        ProjectAccess.IsAllowed(Principal(("auth_method", "mcp_caller")), 1).Should().BeFalse();
        ProjectAccess.IsAllowed(Principal((ClaimTypes.NameIdentifier, "5")), 1).Should().BeTrue("cookie users are not project-restricted");
    }

    [TestCase("/beacon/api/projects/2/imported-documents", HttpStatusCode.Forbidden)]
    [TestCase("/beacon/api/projects/2/imported-documents/9", HttpStatusCode.Forbidden)]
    [TestCase("/beacon/api/projects/1/imported-documents", HttpStatusCode.OK)]
    [TestCase("/beacon/api/projects/1/imported-documents/9", HttpStatusCode.OK)]
    public async Task ImportedDocumentEndpoints_EnforceTheCallersProjects(string path, HttpStatusCode expected)
    {
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<GetImportedDocumentsQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetImportedDocumentsResult([]));
        mediator
            .Setup(x => x.Send(It.IsAny<GetImportedDocumentQuery>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new GetImportedDocumentResult(new ImportedDocumentContent(9, "docs", "a.md", "A", "body", null, DateTime.UtcNow)));

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(mediator.Object);
        builder.Services.AddRouting();
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.User = Principal(("auth_method", "api_key"), ("allowed_projects", "[1]"));
            await next(context);
        });
        app.MapGroup("/beacon/api").MapProjectsEndpoints();
        await app.StartAsync();

        var response = await app.GetTestClient().GetAsync(path);

        response.StatusCode.Should().Be(expected);
    }

    private static ClaimsPrincipal Principal(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(x => new Claim(x.Type, x.Value)), "Test"));
}
