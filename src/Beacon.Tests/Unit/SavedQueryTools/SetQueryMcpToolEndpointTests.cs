using Beacon.Api.Endpoints;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit.SavedQueryTools;

/// <summary>
/// Exposing a query as an MCP tool publishes reviewed SQL to every MCP caller of its projects, so the endpoint is
/// Admin-only. Maps the queries endpoints on a bare app (no database) and reads the endpoint's authorization metadata.
/// </summary>
[TestFixture]
public class SetQueryMcpToolEndpointTests
{
    [Test]
    public async Task SetQueryMcpTool_IsAPutOnTheQuery_AndRequiresTheAdminPolicy()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.Services.AddSingleton(Mock.Of<IMediator>());
        builder.Services.AddAuthorization();
        await using var app = builder.Build();
        app.MapGroup("/beacon/api").MapQueriesEndpoints();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(x => x.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == "SetQueryMcpTool");

        endpoint.RoutePattern.RawText.Should().Be("/beacon/api/queries/{id:int}/mcp-tool");
        endpoint.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods.Should().Equal("PUT");
        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Select(x => x.Policy)
            .Should().Contain(BeaconApiEndpoints.AdminPolicyName);
    }
}
