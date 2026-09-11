using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Beacon.Core.Models;
using Beacon.SampleProject.Middleware;

namespace Beacon.Tests.Unit;

/// <summary>
/// SC4 of spec <c>mcp-project-settings</c>: the API exception middleware maps <see cref="SettingLockedException"/>
/// to HTTP 409 with the <c>/errors/setting-locked</c> problem type, and the arm sits BEFORE the general
/// <see cref="BeaconException"/> arm — a derived type matched by the base arm would come back as 400.
/// </summary>
[TestFixture]
public class ApiExceptionMappingTests
{
    [Test]
    public async Task SettingLockedException_MapsTo409_SettingLocked()
    {
        var (status, problem) = await RunAsync(SettingLockedException.For("EnforceReadOnly", "ForceReadOnly"));

        status.Should().Be(StatusCodes.Status409Conflict);
        problem.GetProperty("type").GetString().Should().Be("/errors/setting-locked");
        problem.GetProperty("status").GetInt32().Should().Be(409);
        problem.GetProperty("title").GetString().Should().Contain("EnforceReadOnly").And.Contain("ForceReadOnly");
    }

    [Test]
    public async Task PlainBeaconException_StillMapsTo400()
    {
        var (status, problem) = await RunAsync(new BeaconException("nope"));

        status.Should().Be(StatusCodes.Status400BadRequest);
        problem.GetProperty("type").GetString().Should().Be("/errors/beacon");
    }

    [Test]
    public async Task InvalidOperation_StillMapsTo400()
    {
        var (status, problem) = await RunAsync(new InvalidOperationException("Project 9 not found."));

        status.Should().Be(StatusCodes.Status400BadRequest);
        problem.GetProperty("type").GetString().Should().Be("/errors/invalid-operation");
    }

    private static async Task<(int Status, JsonElement Problem)> RunAsync(Exception toThrow)
    {
        var middleware = new ApiExceptionMiddleware(_ => throw toThrow, NullLogger<ApiExceptionMiddleware>.Instance);
        var context = new DefaultHttpContext();
        context.Request.Method = "PUT";
        context.Request.Path = "/beacon/api/mcp/settings";
        context.Response.Body = new MemoryStream();

        await middleware.InvokeAsync(context);

        context.Response.Body.Position = 0;
        using var doc = await JsonDocument.ParseAsync(context.Response.Body);

        return (context.Response.StatusCode, doc.RootElement.Clone());
    }
}
