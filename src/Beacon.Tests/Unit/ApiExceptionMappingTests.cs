using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Beacon.Core.Models;
using Beacon.Core.Services;
using Beacon.Api.Middleware;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit;

/// <summary>
/// The API exception middleware's mapping: <see cref="SettingLockedException"/> is a 409 (its arm sits BEFORE the general
/// <see cref="BeaconException"/> arm — a derived type matched by the base arm would come back as 400), a
/// <see cref="BeaconException"/> and an <see cref="InvalidOperationException"/> thrown by Beacon's code are a 400 with their
/// message, and a library's <see cref="InvalidOperationException"/> is a 500 that carries none of its text.
/// </summary>
[TestFixture]
public class ApiExceptionMappingTests
{
    [Test]
    public async Task SettingLockedException_MapsTo409_SettingLocked()
    {
        var (status, problem) = await RunAsync(_ => throw SettingLockedException.For("EnforceReadOnly", "ForceReadOnly"));

        status.Should().Be(StatusCodes.Status409Conflict);
        problem.GetProperty("type").GetString().Should().Be("/errors/setting-locked");
        problem.GetProperty("status").GetInt32().Should().Be(409);
        problem.GetProperty("title").GetString().Should().Contain("EnforceReadOnly").And.Contain("ForceReadOnly");
    }

    [Test]
    public async Task PlainBeaconException_StillMapsTo400()
    {
        var (status, problem) = await RunAsync(_ => throw new BeaconException("nope"));

        status.Should().Be(StatusCodes.Status400BadRequest);
        problem.GetProperty("type").GetString().Should().Be("/errors/beacon");
    }

    [Test]
    public async Task AnInvalidOperationThrownByBeacon_MapsTo400_WithItsMessage()
    {
        var (status, problem) = await RunAsync(_ =>
        {
            SqlIdentifierGuard.Validate("bad;name", "table");
            return Task.CompletedTask;
        });

        status.Should().Be(StatusCodes.Status400BadRequest);
        problem.GetProperty("type").GetString().Should().Be("/errors/invalid-operation");
        problem.GetProperty("title").GetString().Should().Be("Invalid SQL table 'bad;name'.");
    }

    [Test]
    public async Task AnInvalidOperationThrownByBeacon_InAnAwaitedTask_MapsTo400_WithItsMessage()
    {
        var (status, problem) = await RunAsync(async _ => await Task.Run(() => SqlIdentifierGuard.Validate("bad;name", "table")));

        status.Should().Be(StatusCodes.Status400BadRequest);
        problem.GetProperty("title").GetString().Should().Be("Invalid SQL table 'bad;name'.");
    }

    [Test]
    public async Task ALibrarysInvalidOperation_MapsTo500_WithoutItsMessage()
    {
        var (status, problem) = await RunAsync(x =>
        {
            _ = new List<string>().First();
            return Task.CompletedTask;
        });

        AssertInternalError(status, problem);
    }

    [Test]
    public async Task ALibrarysInvalidOperation_InAnAwaitedTask_MapsTo500_WithoutItsMessage()
    {
        var (status, problem) = await RunAsync(async _ => await Task.Run(() => new List<string>().Single()));

        AssertInternalError(status, problem);
    }

    [Test]
    public async Task ALibrarysInvalidOperationSubtype_MapsTo500_WithoutItsMessage()
    {
        var stream = new MemoryStream();
        await stream.DisposeAsync();

        var (status, problem) = await RunAsync(_ =>
        {
            stream.WriteByte(1);
            return Task.CompletedTask;
        });

        AssertInternalError(status, problem);
    }

    [Test]
    public async Task AnInvalidOperationThatWasNeverThrownBeforeItsTaskFaulted_MapsTo500()
    {
        var (status, problem) = await RunAsync(_ => Task.FromException(new InvalidOperationException("Connection to db-internal-7 failed.")));

        AssertInternalError(status, problem);
    }

    [Test]
    public async Task AnInternalError_IsLoggedWithItsStackFrames_NeverItsMessage()
    {
        var log = new LogRecorder();

        var (status, _) = await RunAsync(_ =>
        {
            FailInternally();
            return Task.CompletedTask;
        }, log);

        status.Should().Be(StatusCodes.Status500InternalServerError);
        var entry = log.Entries.Should().ContainSingle().Which;
        entry.Level.Should().Be(LogLevel.Error);
        entry.Message.Should().Contain("Unhandled exception in PUT /beacon/api/mcp/settings (IOException (error code none")
            .And.Contain("stack: IOException at ApiExceptionMappingTests.FailInternally, ");
        log.Contains("db-internal-7").Should().BeFalse();
        entry.Exception.Should().BeNull();
    }

    private static void FailInternally()
    {
        throw new IOException("Connection to db-internal-7 failed.");
    }

    private static void AssertInternalError(int status, JsonElement problem)
    {
        status.Should().Be(StatusCodes.Status500InternalServerError);
        problem.GetProperty("type").GetString().Should().Be("/errors/internal");
        problem.GetProperty("title").GetString().Should().Be("Internal server error");
    }

    private static async Task<(int Status, JsonElement Problem)> RunAsync(RequestDelegate next, LogRecorder? log = null)
    {
        var middleware = new ApiExceptionMiddleware(next, log?.For<ApiExceptionMiddleware>() ?? NullLogger<ApiExceptionMiddleware>.Instance);
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
