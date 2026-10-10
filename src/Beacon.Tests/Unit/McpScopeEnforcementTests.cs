using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Beacon.Api.Endpoints;
using Beacon.Core.Authorization;
using Beacon.Core.Configuration;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Mcp;
using Beacon.Core.Models;
using Beacon.MCP;
using Beacon.MCP.Services;
using Beacon.Tests.Common;
using Beacon.Tests.Unit.HostEndpoints;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Moq;
using NUnit.Framework;

namespace Beacon.Tests.Unit;

/// <summary>
/// §1.4 — the Execute scope on <c>/beacon/mcp</c>: <see cref="BeaconMcpEndpointRouteBuilderExtensions.MapBeaconMcp"/>
/// maps the route behind the Execute-scope policy; inside the MCP layer <see cref="McpScopeMessageFilter"/> refuses every
/// other request and <see cref="McpScopeCallToolFilter"/> every tool call of a scoped caller without Execute, or of an
/// unauthenticated caller over HTTP, even when a host maps the route with a weaker policy. A refused tool call is
/// audited (§1.7/§9.5).
/// </summary>
[TestFixture]
public class McpScopeEnforcementTests
{
    private const string ToolName = "query";

    [TestCase("api_key", "Read")]
    [TestCase("api_key", null)]
    [TestCase("mcp_caller", "Read")]
    [TestCase("mcp_caller", null)]
    public async Task ScopedCallerWithoutExecute_IsRefused_BeforeTheTool(string authMethod, string? scope)
    {
        var toolRan = false;

        var result = await InvokeAsync(ScopedCaller(authMethod, scope), () => toolRan = true);

        toolRan.Should().BeFalse();
        result.IsError.Should().BeTrue();
        HostEndpointDispatchTests.Text(result).Should().Be(McpScopeCallToolFilter.MissingScopeMessage);
    }

    [TestCase("api_key")]
    [TestCase("mcp_caller")]
    public async Task ScopedCallerWithExecute_ReachesTheTool(string authMethod)
    {
        var toolRan = false;

        var result = await InvokeAsync(ScopedCaller(authMethod, "Execute"), () => toolRan = true);

        toolRan.Should().BeTrue();
        result.IsError.Should().NotBe(true);
    }

    [Test]
    public async Task CallerWithoutAScopeMarker_ReachesTheTool()
    {
        var toolRan = false;
        var cookie = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "ext-ada")], "Beacon.Auth"));

        await InvokeAsync(cookie, () => toolRan = true);

        toolRan.Should().BeTrue();
    }

    [Test]
    public async Task RawAdminScopeClaim_IsNotExecute()
    {
        var toolRan = false;

        var result = await InvokeAsync(ScopedCaller("api_key", "Admin"), () => toolRan = true);

        toolRan.Should().BeFalse("the key middleware turns a stored Admin scope into Execute; the raw claim grants nothing");
        result.IsError.Should().BeTrue();
    }

    [Test]
    public async Task NoUserAndNoHttpRequest_ReachesTheTool()
    {
        // A transport without authentication (stdio): there is no caller to check.
        var toolRan = false;

        var result = await InvokeAsync(null, () => toolRan = true);

        toolRan.Should().BeTrue();
        result.IsError.Should().NotBe(true);
    }

    [Test]
    public async Task NoUserOverHttp_IsRefused()
    {
        var toolRan = false;
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext() })
            .BuildServiceProvider();

        var result = await InvokeAsync(null, () => toolRan = true, services);

        toolRan.Should().BeFalse();
        HostEndpointDispatchTests.Text(result).Should().Be(McpScopeCallToolFilter.UnauthenticatedMessage);
    }

    [Test]
    public async Task UnauthenticatedUser_IsRefused()
    {
        var toolRan = false;
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity([new Claim(McpCallerClaimTypes.Scope, "Execute")]));

        var result = await InvokeAsync(anonymous, () => toolRan = true);

        toolRan.Should().BeFalse();
        HostEndpointDispatchTests.Text(result).Should().Be(McpScopeCallToolFilter.UnauthenticatedMessage);
    }

    [Test]
    public async Task RefusedCall_IsAudited_WithTheToolTheKeyAndAPermissionError()
    {
        var auditLogs = new List<McpAuditLog>();
        var caller = ScopedCaller("api_key", "Read");
        using var provider = AuditingServices(auditLogs, required: false, caller: caller);
        using var scope = provider.CreateScope();

        var result = await InvokeAsync(caller, () => { }, scope.ServiceProvider);

        HostEndpointDispatchTests.Text(result).Should().Be(McpScopeCallToolFilter.MissingScopeMessage);
        var row = auditLogs.Should().ContainSingle().Subject;
        row.Tool.Should().Be(ToolName);
        row.ApiKeyId.Should().Be(3);
        row.UserId.Should().Be(7);
        row.ErrorMessage.Should().Be(McpScopeCallToolFilter.MissingScopeMessage);
        row.ResultRowCount.Should().BeNull();
    }

    [Test]
    public async Task RefusedCall_WhoseAuditCannotBeWritten_IsWithheld_WhenTheAuditIsRequired()
    {
        var caller = ScopedCaller("api_key", "Read");
        using var provider = AuditingServices([], required: true, failSave: true, caller: caller);
        using var scope = provider.CreateScope();

        var result = await InvokeAsync(caller, () => { }, scope.ServiceProvider);

        result.IsError.Should().BeTrue();
        HostEndpointDispatchTests.Text(result).Should().Be(McpAuditCallToolFilter.WithheldMessage);
    }

    [Test]
    public void AddBeaconMcp_RegistersTheScopeMessageFilter()
    {
        var registration = new ServiceCollection();
        registration.AddLogging();
        Beacon.MCP.ServiceConfiguration.AddBeaconMcp(registration);
        using var provider = registration.BuildServiceProvider();

        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<McpServerOptions>>().Value;

        options.Filters.Message.IncomingFilters.Should().ContainSingle();
    }

    [TestCase("tools/list")]
    [TestCase("resources/list")]
    [TestCase("resources/read")]
    [TestCase("prompts/list")]
    [TestCase("completion/complete")]
    [TestCase("logging/setLevel")]
    [TestCase("anything/else")]
    public async Task MessageFilter_RefusesEveryOtherRequestOfACallerWithoutExecute(string method)
    {
        var nextRan = false;

        var act = () => InvokeMessageFilterAsync(new JsonRpcRequest { Method = method }, ScopedCaller("api_key", "Read"), () => nextRan = true);

        (await act.Should().ThrowAsync<McpProtocolException>())
            .Which.Should().Match<McpProtocolException>(x => x.ErrorCode == McpErrorCode.InvalidRequest && x.Message == McpScopeCallToolFilter.MissingScopeMessage);
        nextRan.Should().BeFalse();
    }

    [TestCase("initialize")]
    [TestCase("ping")]
    [TestCase("tools/call")]
    public async Task MessageFilter_LetsTheHandshakePingAndToolCallsThrough(string method)
    {
        var nextRan = false;

        await InvokeMessageFilterAsync(new JsonRpcRequest { Method = method }, ScopedCaller("api_key", "Read"), () => nextRan = true);

        nextRan.Should().BeTrue("tools/call is refused and audited by the call-tool filter; the other two carry no data");
    }

    [Test]
    public async Task MessageFilter_LetsNotificationsThrough()
    {
        var nextRan = false;

        await InvokeMessageFilterAsync(new JsonRpcNotification { Method = "notifications/initialized" }, ScopedCaller("api_key", "Read"), () => nextRan = true);

        nextRan.Should().BeTrue();
    }

    [Test]
    public async Task MessageFilter_LetsAnExecuteCallerThrough()
    {
        var nextRan = false;

        await InvokeMessageFilterAsync(new JsonRpcRequest { Method = "tools/list" }, ScopedCaller("mcp_caller", "Execute"), () => nextRan = true);

        nextRan.Should().BeTrue();
    }

    [Test]
    public async Task MessageFilter_RefusesAnUnauthenticatedCallerOverHttp()
    {
        var services = new ServiceCollection()
            .AddSingleton<IHttpContextAccessor>(new HttpContextAccessor { HttpContext = new DefaultHttpContext() })
            .BuildServiceProvider();

        var act = () => InvokeMessageFilterAsync(new JsonRpcRequest { Method = "tools/list" }, null, () => { }, services);

        (await act.Should().ThrowAsync<McpProtocolException>())
            .Which.Message.Should().Be(McpScopeCallToolFilter.UnauthenticatedMessage);
    }

    [Test]
    public async Task AddBeaconMcp_RunsTheScopeFilterOutermost_SoARefusedCallReachesNoHandler_AndIsAudited()
    {
        var auditLogs = new List<McpAuditLog>();
        var registration = AuditingRegistration(auditLogs, required: false);
        Beacon.MCP.ServiceConfiguration.AddBeaconMcp(registration);
        var handlerRan = false;
        registration.Configure<McpServerOptions>(x => x.Handlers.CallToolHandler = (_, _) =>
        {
            handlerRan = true;
            return ValueTask.FromResult(new CallToolResult());
        });
        using var provider = registration.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<McpServerOptions>>().Value;

        // Composed as the SDK composes it: the first filter is the outermost.
        var pipeline = options.Handlers.CallToolHandler!;
        for (var i = options.Filters.Request.CallToolFilters.Count - 1; i >= 0; i--)
        {
            pipeline = options.Filters.Request.CallToolFilters[i](pipeline);
        }

        using var scope = provider.CreateScope();
        var request = new RequestContext<CallToolRequestParams>(
            Mock.Of<McpServer>(),
            new JsonRpcRequest { Method = "tools/call" },
            new CallToolRequestParams { Name = ToolName })
        {
            Services = scope.ServiceProvider,
            User = ScopedCaller("api_key", "Read")
        };
        var result = await pipeline(request, CancellationToken.None);

        handlerRan.Should().BeFalse();
        HostEndpointDispatchTests.Text(result).Should().Be(McpScopeCallToolFilter.MissingScopeMessage);
        auditLogs.Should().ContainSingle().Which.Tool.Should().Be(ToolName);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReadScopedCaller_IsRefused_EvenWhenTheHostMapsTheRouteWithoutTheExecutePolicy(bool bareRequireAuthorization)
    {
        var caller = new McpCaller(McpCallerKind.System, "reporting", HostEndpointTestHost.CallerHash, null, [HostEndpointTestHost.ProjectId], McpCallerScope.Read, [HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings)]);
        await using var host = await StartHostAsync(caller, bareRequireAuthorization);
        await using var client = await ConnectAsync(host);

        var result = await client.CallToolAsync("api_thing_by_id", new Dictionary<string, object?> { ["id"] = 3 });

        result.IsError.Should().BeTrue();
        HostEndpointDispatchTests.Text(result).Should().Be(McpScopeCallToolFilter.MissingScopeMessage);
        var row = host.AuditLogs.Should().ContainSingle("the refused call reached no tool but is audited").Subject;
        row.Tool.Should().Be("api_thing_by_id");
        row.CallerKind.Should().Be(nameof(McpCallerKind.System));
        row.ErrorMessage.Should().Be(McpScopeCallToolFilter.MissingScopeMessage);
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task ReadScopedCaller_GetsNoToolList_OnAWeakMapping(bool bareRequireAuthorization)
    {
        var caller = new McpCaller(McpCallerKind.System, "reporting", HostEndpointTestHost.CallerHash, null, [HostEndpointTestHost.ProjectId], McpCallerScope.Read, [HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings)]);
        await using var host = await StartHostAsync(caller, bareRequireAuthorization);
        await using var client = await ConnectAsync(host);

        var act = async () => await client.ListToolsAsync();

        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("Execute scope");
    }

    [Test]
    public async Task UnauthenticatedCaller_IsRefused_OnAMappingWithoutAuthorization()
    {
        await using var host = await HostEndpointTestHost.StartAsync(withMcpServer: true);
        await using var client = await ConnectAsync(host);

        var tools = async () => await client.ListToolsAsync();
        var call = await client.CallToolAsync("api_thing_by_id", new Dictionary<string, object?> { ["id"] = 3 });

        (await tools.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("authenticated caller");
        call.IsError.Should().BeTrue();
        HostEndpointDispatchTests.Text(call).Should().Be(McpScopeCallToolFilter.UnauthenticatedMessage);
        host.AuditLogs.Should().ContainSingle().Which.Tool.Should().Be("api_thing_by_id");
    }

    [Test]
    public async Task ExecuteScopedCaller_IsServed_OnTheSameWeakMapping()
    {
        var caller = new McpCaller(McpCallerKind.System, "routine-runner", HostEndpointTestHost.CallerHash, null, [HostEndpointTestHost.ProjectId], McpCallerScope.Execute, [HostEndpointTestClaims.Permission(HostEndpointTestHost.ViewThings)]);
        await using var host = await StartHostAsync(caller, bareRequireAuthorization: true);
        await using var client = await ConnectAsync(host);

        var result = await client.CallToolAsync("api_thing_by_id", new Dictionary<string, object?> { ["id"] = 3 });

        result.IsError.Should().NotBe(true, HostEndpointDispatchTests.Text(result));
    }

    [Test]
    public async Task MapBeaconMcp_RequiresTheExecuteScopePolicy_OnEveryMcpEndpoint()
    {
        await using var app = await StartMappedAsync();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(x => x.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(x => x.RoutePattern.RawText?.StartsWith(McpDiscoveryPaths.McpPath, StringComparison.Ordinal) == true)
            .ToList();

        endpoints.Should().NotBeEmpty();
        endpoints.Should().OnlyContain(x => x.Metadata
            .GetOrderedMetadata<IAuthorizeData>()
            .Any(y => y.Policy == BeaconScopes.ExecuteScopePolicyName));
    }

    [Test]
    public void SampleHost_MapsMcpWithMapBeaconMcp_NeverWithABareMapMcp()
    {
        // The sample host does not boot without a database, so its MCP mapping is checked in its source. Together with
        // MapBeaconMcp_RequiresTheExecuteScopePolicy_OnEveryMcpEndpoint this proves every /beacon/mcp endpoint the host
        // maps carries the Execute-scope policy.
        var code = File.ReadAllLines(SampleHostProgramPath())
            .Select(x => x.Trim())
            .Where(x => !x.StartsWith("//", StringComparison.Ordinal))
            .ToList();

        code.Should().Contain("app.MapBeaconMcp();");
        code.Should().NotContain(x => Regex.IsMatch(x, @"\.MapMcp\s*[<(]"), "a bare MapMcp skips the Execute-scope policy");
    }

    [TestCase("Read", HttpStatusCode.Forbidden)]
    [TestCase(null, HttpStatusCode.Forbidden)]
    public async Task MapBeaconMcp_RefusesAScopedCallerWithoutExecute_AtTheDoor(string? scope, HttpStatusCode expected)
    {
        await using var app = await StartMappedAsync(ScopedCaller("api_key", scope));

        var response = await app.GetTestClient().PostAsync(
            McpDiscoveryPaths.McpPath,
            new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(expected);
    }

    [Test]
    public async Task MapBeaconMcp_ChallengesAnUnauthenticatedCaller()
    {
        await using var app = await StartMappedAsync();

        var response = await app.GetTestClient().PostAsync(
            McpDiscoveryPaths.McpPath,
            new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Test]
    public async Task MapBeaconMcp_LetsAnExecuteScopedCallerThrough()
    {
        await using var app = await StartMappedAsync(ScopedCaller("api_key", "Execute"));

        var response = await app.GetTestClient().PostAsync(
            McpDiscoveryPaths.McpPath,
            new StringContent("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/list\"}", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    private static string SampleHostProgramPath()
    {
        var directory = new DirectoryInfo(TestContext.CurrentContext.TestDirectory);
        while (directory != null && !File.Exists(System.IO.Path.Combine(directory.FullName, "Beacon.slnx")))
        {
            directory = directory.Parent;
        }

        directory.Should().NotBeNull("the tests run inside the repository");
        return System.IO.Path.Combine(directory!.FullName, "src", "Beacon.SampleProject", "Program.cs");
    }

    private static ClaimsPrincipal ScopedCaller(string authMethod, string? scope)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "7"),
            new(McpCallerClaimTypes.AuthMethod, authMethod),
            new(McpCallerClaimTypes.ApiKeyId, "3"),
            new(McpCallerClaimTypes.AllowedProjects, "[7]")
        };
        if (scope != null)
        {
            claims.Add(new Claim(McpCallerClaimTypes.Scope, scope));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, authMethod == "api_key" ? "ApiKey" : "Bearer"));
    }

    private static async Task<CallToolResult> InvokeAsync(ClaimsPrincipal? user, Action onTool, IServiceProvider? services = null)
    {
        var handler = McpScopeCallToolFilter.Create()((_, _) =>
        {
            onTool();
            return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "rows" }] });
        });
        var request = new RequestContext<CallToolRequestParams>(
            Mock.Of<McpServer>(),
            new JsonRpcRequest { Method = "tools/call" },
            new CallToolRequestParams { Name = ToolName })
        {
            Services = services ?? new ServiceCollection().AddLogging().BuildServiceProvider(),
            User = user
        };

        return await handler(request, CancellationToken.None);
    }

    private static async Task InvokeMessageFilterAsync(JsonRpcMessage message, ClaimsPrincipal? user, Action onNext, IServiceProvider? services = null)
    {
        var handler = McpScopeMessageFilter.Create()((_, _) =>
        {
            onNext();
            return Task.CompletedTask;
        });
        var context = new MessageContext(Mock.Of<McpServer>(), message)
        {
            Services = services ?? new ServiceCollection().AddLogging().BuildServiceProvider(),
            User = user
        };

        await handler(context, CancellationToken.None);
    }

    // The audit service's dependencies with a capturing context: no database (§4.7).
    private static ServiceCollection AuditingRegistration(List<McpAuditLog> auditLogs, bool required, bool failSave = false, ClaimsPrincipal? caller = null)
    {
        var registration = new ServiceCollection();
        registration.AddLogging();
        var factory = new Mock<IDbContextFactory<BeaconContext>>();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AuditCapturingContext(auditLogs, failSave));
        registration.AddSingleton(factory.Object);
        registration.AddSingleton(SettingsProviderMock.Create(new McpSettingsData { RetainQueryContent = true }).Object);
        // The audit row reads the caller's identifiers off the HTTP request, as it does in production.
        registration.AddSingleton<IHttpContextAccessor>(caller == null
            ? new HttpContextAccessor()
            : new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = caller } });
        registration.Configure<McpDeploymentOptions>(x => x.Audit = new McpAuditOptions { Required = required });
        return registration;
    }

    private static ServiceProvider AuditingServices(List<McpAuditLog> auditLogs, bool required, bool failSave = false, ClaimsPrincipal? caller = null)
    {
        var registration = AuditingRegistration(auditLogs, required, failSave, caller);
        registration.AddTransient<McpAuditService>();
        registration.AddScoped<McpAuditOutcome>();
        registration.AddScoped<McpProjectContext>();
        registration.AddScoped<IProjectContext>(x => new McpProjectContext { UserId = 7 });
        return registration.BuildServiceProvider();
    }

    // The principal JwtBearerAuthMiddleware mints for a mapped caller on /beacon/mcp (with its auth_method marker).
    private static Task<HostEndpointTestHost> StartHostAsync(McpCaller caller, bool bareRequireAuthorization) =>
        HostEndpointTestHost.StartAsync(
            withMcpServer: true,
            requireAuthorizationOnMcp: bareRequireAuthorization,
            beforeMcp: x => x.Use(async (context, next) =>
            {
                context.User = HostEndpointTestHost.McpPrincipal(caller);
                context.Items[typeof(McpCaller)] = caller;
                await next(context);
            }));

    private static async Task<McpClient> ConnectAsync(HostEndpointTestHost host)
    {
        var httpClient = host.App.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri(httpClient.BaseAddress!, McpDiscoveryPaths.McpPath),
                TransportMode = HttpTransportMode.StreamableHttp
            },
            httpClient,
            NullLoggerFactory.Instance,
            ownsHttpClient: true);

        return await McpClient.CreateAsync(transport);
    }

    private static async Task<WebApplication> StartMappedAsync(ClaimsPrincipal? user = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddMcpServer().WithHttpTransport();
        builder.Services.AddBeaconApiAuthorization();
        builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, StatusCodeResultHandler>();

        var app = builder.Build();
        app.Use((context, next) =>
        {
            if (user != null)
            {
                context.User = user;
            }

            return next(context);
        });
        app.UseRouting();
        app.UseAuthorization();
        app.MapBeaconMcp();
        await app.StartAsync();

        return app;
    }

    /// <summary>Captures the audit rows the service adds, or fails the save; no database (§4.7).</summary>
    private sealed class AuditCapturingContext : BeaconContext
    {
        private static readonly DbContextOptions<AuditCapturingContext> ContextOptions =
            new DbContextOptionsBuilder<AuditCapturingContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Mock<DbSet<McpAuditLog>> _set = new();
        private readonly bool _failSave;

        public AuditCapturingContext(List<McpAuditLog> logs, bool failSave) : base(ContextOptions, "beacon")
        {
            _failSave = failSave;
            _set.Setup(x => x.Add(It.IsAny<McpAuditLog>())).Callback<McpAuditLog>(logs.Add);
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpAuditLog))
            {
                return (DbSet<TEntity>)(object)_set.Object;
            }

            return base.Set<TEntity>();
        }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            _failSave ? throw new InvalidOperationException("audit sink unavailable") : Task.FromResult(0);
    }

    /// <summary>Writes 401/403 directly so no authentication scheme is needed for Forbid/Challenge.</summary>
    private sealed class StatusCodeResultHandler : IAuthorizationMiddlewareResultHandler
    {
        public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
        {
            if (authorizeResult.Succeeded)
            {
                return next(context);
            }

            context.Response.StatusCode = authorizeResult.Forbidden
                ? StatusCodes.Status403Forbidden
                : StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        }
    }
}
