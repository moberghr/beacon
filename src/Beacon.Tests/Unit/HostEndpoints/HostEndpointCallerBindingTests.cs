using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Beacon.MCP.HostEndpoints;
using static Beacon.Tests.Unit.HostEndpoints.HostEndpointDispatchTests;

namespace Beacon.Tests.Unit.HostEndpoints;

public sealed record CallerBoundLeaveBody(int RequestingUserId, string Reason);

/// <summary>
/// An argument bound from the caller (<see cref="HostEndpointToolOptions.BindFromCaller(string, string)"/>): the agent
/// cannot know the person's id in the host, so the tool leaves it out of its schema and the call carries the caller's
/// own value — a query value, or a property of the JSON body written over whatever the agent sent.
/// </summary>
[TestFixture]
public class HostEndpointCallerBindingTests
{
    private const string UserIdClaim = "app_user_id";

    [Test]
    public async Task ABoundQueryValue_IsLeftOutOfTheSchema()
    {
        await using var host = await StartAsync();

        var properties = host.Tool("my_assets").InputSchema.GetProperty("properties");

        properties.EnumerateObject().Select(x => x.Name).Should().Equal("state");
    }

    [Test]
    public async Task ABoundQueryValue_IsTheCallersOwn()
    {
        await using var host = await StartAsync();

        var result = await Service(host, Caller("42")).CallAsync("api_my_assets", Args(new { state = "Approved" }), CancellationToken.None);

        result.IsError.Should().NotBe(true, Text(result));
        result.StructuredContent!.Value.GetProperty("userId").GetInt32().Should().Be(42);
        result.StructuredContent!.Value.GetProperty("state").GetString().Should().Be("Approved");
    }

    [Test]
    public async Task AnAgentSendingABoundValue_IsRefused()
    {
        await using var host = await StartAsync();

        var result = await Service(host, Caller("42")).CallAsync("api_my_assets", Args(new { userId = 7 }), CancellationToken.None);

        result.IsError.Should().BeTrue();
        Text(result).Should().Contain("unknown argument(s) userId");
    }

    [Test]
    public async Task ABoundBodyProperty_IsLeftOutOfTheBodysSchema_AndOfItsRequiredList()
    {
        await using var host = await StartAsync();

        var body = host.Tool("request_leave").InputSchema.GetProperty("properties").GetProperty("body");

        body.GetProperty("properties").EnumerateObject().Select(x => x.Name).Should().Equal("reason");
        body.TryGetProperty("required", out var required).Should().BeTrue();
        required.EnumerateArray().Select(x => x.GetString()).Should().NotContain("requestingUserId");
    }

    [Test]
    public async Task ABoundBodyProperty_IsTheCallersOwn()
    {
        await using var host = await StartAsync();
        var arguments = Args(new { body = new { reason = "Holiday" } });

        var result = await Service(host, Caller("42")).CallAsync("api_request_leave", arguments, CancellationToken.None);

        result.IsError.Should().NotBe(true, Text(result));
        result.StructuredContent!.Value.GetProperty("requestingUserId").GetInt32().Should().Be(42, "the caller's own id, written as the integer the body declares");
        result.StructuredContent!.Value.GetProperty("reason").GetString().Should().Be("Holiday");
    }

    [TestCase("requestingUserId")]
    [TestCase("RequestingUserId")]
    [TestCase("REQUESTINGUSERID")]
    public async Task AnAgentSendingABoundBodyProperty_InAnyCasing_IsRefused(string key)
    {
        await using var host = await StartAsync();
        var arguments = Args(new Dictionary<string, object?> { ["body"] = new Dictionary<string, object?> { ["reason"] = "Holiday", [key] = 7 } });

        var result = await Service(host, Caller("42")).CallAsync("api_request_leave", arguments, CancellationToken.None);

        result.IsError.Should().BeTrue("JSON binding ignores case, so any casing would compete with the caller's value");
        Text(result).Should().Contain("is filled from the caller and cannot be sent");
    }

    [Test]
    public async Task ARawFieldNamedAfterABoundArgument_IsRefused_EvenWhereTheToolBindsNone()
    {
        await using var host = await HostEndpointTestHost.StartAsync(configure: x => x.BindFromCaller("pageSize", UserIdClaim));
        var caller = HostEndpointTestHost.SystemCaller(HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewLoans), new Claim(UserIdClaim, "42"));

        var result = await host.CreateService(HostEndpointTestHost.OuterRequest(caller))
            .CallAsync("api_loan_page", Args(new { request = new { page = 2, pageSize = 50 } }), CancellationToken.None);

        result.IsError.Should().BeTrue("a custom binder could read the field the host binds from the caller");
        Text(result).Should().Contain("pageSize is filled from the caller and cannot be sent");
    }

    [Test]
    public async Task ABindingNoToolTakes_IsWarnedAtStartup()
    {
        var logs = new Beacon.Tests.Common.LogRecorder();

        await using var host = await HostEndpointTestHost.StartAsync(
            [],
            mapDefaultMinimalApis: false,
            configure: x => x.BindFromCaller("usreId", UserIdClaim),
            services: x => x.AddSingleton<Microsoft.Extensions.Logging.ILoggerProvider>(logs),
            map: x => x.MapGet("/my/assets", (int? userId) => Results.Ok(new { userId }))
                .RequireAuthorization(HostEndpointTestHost.ViewThingPolicy)
                .WithBeaconTool("my_assets", "The caller's assets.", readOnly: true));

        logs.Contains("BindFromCaller names usreId, which no endpoint tool takes").Should().BeTrue();
    }

    [Test]
    public async Task ACallerWithoutAValue_IsRefused_AndAudited()
    {
        await using var host = await StartAsync();

        var result = await Service(host, Caller(null)).CallAsync("api_my_assets", null, CancellationToken.None);

        result.IsError.Should().BeTrue();
        Text(result).Should().Be("Forbidden: the host application gives this caller no value for 'userId'.");
        host.AuditLogs.Should().ContainSingle().Which.ErrorMessage.Should().Be(Text(result));
    }

    [Test]
    public void BindingOneArgumentTwice_IsRefused()
    {
        var options = new HostEndpointToolOptions().BindFromCaller("userId", UserIdClaim);

        var act = () => options.BindFromCaller("UserId", "other");

        act.Should().Throw<InvalidOperationException>().WithMessage("*'UserId' is already bound from the caller*");
    }

    private static HostEndpointToolService Service(HostEndpointTestHost host, Beacon.Core.Mcp.McpCaller caller) =>
        host.CreateService(HostEndpointTestHost.OuterRequest(caller));

    private static Beacon.Core.Mcp.McpCaller Caller(string? userId)
    {
        List<Claim> claims = [HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings)];
        if (userId != null)
        {
            claims.Add(new Claim(UserIdClaim, userId));
        }

        return HostEndpointTestHost.UserCaller(null, [.. claims]);
    }

    private static Task<HostEndpointTestHost> StartAsync() =>
        HostEndpointTestHost.StartAsync(
            [],
            mapDefaultMinimalApis: false,
            configure: x => x
                .BindFromCaller("userId", UserIdClaim)
                .BindFromCaller("requestingUserId", UserIdClaim),
            map: x =>
            {
                x.MapGet("/my/assets", (int? userId, string? state) => Results.Ok(new { userId, state }))
                    .RequireAuthorization(HostEndpointTestHost.ViewThingPolicy)
                    .WithBeaconTool("my_assets", "The caller's assets.", readOnly: true);
                x.MapPost("/my/leave", (CallerBoundLeaveBody body) => Results.Ok(new { body.RequestingUserId, body.Reason }))
                    .RequireAuthorization(HostEndpointTestHost.ViewThingPolicy)
                    .WithBeaconTool("request_leave", "Requests leave for the caller.", readOnly: true);
            });
}
