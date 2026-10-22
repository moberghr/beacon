using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Beacon.Core.Authorization;
using Beacon.Api.Endpoints;
using Beacon.Core.Data.Enums;
using Beacon.Core.Handlers.AiActors;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// <c>POST /beacon/api/ai-actors/</c> over a TestServer with a mocked mediator: whether a new actor's changes wait for
/// approval is read from the body and comes back in the response.
/// </summary>
[TestFixture]
public class AiActorCreateEndpointTests
{
    [TestCase(false)]
    [TestCase(true)]
    public async Task Create_RequiresApproval_RoundTripsThroughTheEndpoint(bool requiresApproval)
    {
        CreateAiActorCommand? sent = null;
        var mediator = new Mock<IMediator>();
        mediator
            .Setup(x => x.Send(It.IsAny<CreateAiActorCommand>(), It.IsAny<CancellationToken>()))
            .Callback<IRequest<CreateAiActorResult>, CancellationToken>((command, _) => sent = (CreateAiActorCommand)command)
            .ReturnsAsync((IRequest<CreateAiActorResult> command, CancellationToken _) =>
                new CreateAiActorResult
                {
                    ActorId = 5,
                    Name = "watcher",
                    Status = AiActorStatus.Active,
                    DataSourceId = 1,
                    RequiresApproval = ((CreateAiActorCommand)command).RequiresApproval ?? true
                });
        await using var app = await StartAsync(mediator.Object);
        using var client = app.GetTestClient();

        using var response = await client.PostAsJsonAsync(
            "/beacon/api/ai-actors/",
            new { name = "watcher", instructions = "watch", dataSourceId = 1, requiresApproval });

        response.EnsureSuccessStatusCode();
        sent!.RequiresApproval.Should().Be(requiresApproval);
        var body = await response.Content.ReadFromJsonAsync<JsonObject>();
        body!["requiresApproval"]!.GetValue<bool>().Should().Be(requiresApproval);
    }

    private static async Task<WebApplication> StartAsync(IMediator mediator)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(mediator);
        builder.Services.AddSingleton(Mock.Of<IActorUserResolver>());
        builder.Services.AddRouting();
        var app = builder.Build();
        app.MapGroup("/beacon/api").MapAiActorsEndpoints();
        await app.StartAsync();

        return app;
    }
}
