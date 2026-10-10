using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Beacon.Api.Endpoints;
using Beacon.Core.Models;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.ProjectAuthorization;

/// <summary>
/// The API group maps Beacon's access outcomes itself, in a host without the sample's exception middleware: a refused
/// action is a 403 and an unreadable or missing resource a 404, both RFC 7807 problems that carry no exception message;
/// host-managed membership is a 409 and too many searches a 429. Any other exception, an
/// <see cref="UnauthorizedAccessException"/> from file access included, is left to the host. Each mapping is logged with
/// the exception type, the route name and the route's ids, never the message.
/// </summary>
[TestFixture]
public class AccessExceptionMappingTests
{
    private const int HostHandled = 599;

    [Test]
    public async Task RefusedAndMissing_Map_To403And404_WithoutTheMessage()
    {
        await using var app = await StartAsync(new LogRecorder());
        var client = app.GetTestClient();

        var forbidden = await client.GetAsync("/beacon/api/forbidden/7");
        var missing = await client.GetAsync("/beacon/api/missing");

        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        forbidden.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        (await client.GetStringAsync("/beacon/api/ok")).Should().Be("fine");
        (await forbidden.Content.ReadAsStringAsync()).Should().NotContain("secret-project");
        (await missing.Content.ReadAsStringAsync()).Should().NotContain("secret-project");
    }

    [Test]
    public async Task ConflictAndRateLimit_Map_To409And429_WithTheirMessage()
    {
        await using var app = await StartAsync(new LogRecorder());
        var client = app.GetTestClient();

        var conflict = await client.GetAsync("/beacon/api/conflict");
        var limited = await client.GetAsync("/beacon/api/limited");

        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        using var problem = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync());
        problem.RootElement.GetProperty("title").GetString().Should().Be("Project membership is managed by the host.");
    }

    [Test]
    public async Task AnUnauthorizedAccessException_IsLeftToTheHost()
    {
        await using var app = await StartAsync(new LogRecorder());
        var client = app.GetTestClient();

        var response = await client.GetAsync("/beacon/api/file-denied");

        ((int)response.StatusCode).Should().Be(HostHandled, "only Beacon's own refusals are mapped to 403 by the API");
        (await response.Content.ReadAsStringAsync()).Should().Be(nameof(UnauthorizedAccessException));
    }

    [Test]
    public async Task EachMapping_IsLogged_WithTheTypeRouteAndIds_ButNotTheMessage()
    {
        var logs = new LogRecorder();
        await using var app = await StartAsync(logs);
        var client = app.GetTestClient();

        await client.GetAsync("/beacon/api/forbidden/7");

        var mapping = logs.Entries
            .Where(x => x.Category == typeof(BeaconAccessExceptionEndpointFilter).FullName)
            .Should().ContainSingle().Subject;
        mapping.Level.Should().Be(LogLevel.Information);
        mapping.Message.Should().Contain(nameof(BeaconForbiddenException)).And.Contain("403").And.Contain("RefuseSeven").And.Contain("id=7");
        mapping.FullText.Should().NotContain("secret-project");
    }

    private static async Task<WebApplication> StartAsync(LogRecorder logs)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddRouting();
        builder.Services.AddProblemDetails();
        var app = builder.Build();

        // Stands in for the host's own exception handling.
        app.Use(async (context, next) =>
        {
            try
            {
                await next(context);
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = HostHandled;
                await context.Response.WriteAsync(ex.GetType().Name);
            }
        });

        var api = app.MapGroup("/beacon/api").AddEndpointFilter<BeaconAccessExceptionEndpointFilter>();
        api.MapGet("/forbidden/{id:int}", IResult (int id) => throw new BeaconForbiddenException($"secret-project {id} refused")).WithName("RefuseSeven");
        api.MapGet("/missing", IResult () => throw new BeaconNotFoundException("secret-project 7 not found"));
        api.MapGet("/conflict", IResult () => throw new BeaconConflictException("Project membership is managed by the host."));
        api.MapGet("/limited", IResult () => throw new BeaconRateLimitedException("Too many member searches."));
        api.MapGet("/file-denied", IResult () => throw new UnauthorizedAccessException("secret-project path denied"));
        api.MapGet("/ok", () => "fine");
        await app.StartAsync();

        return app;
    }
}
