using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Beacon.Core.Data;
using Beacon.Core.Data.Entities;
using Beacon.Core.Mcp;
using Beacon.Core.Models;
using Beacon.MCP.HostEndpoints;
using Beacon.MCP.Services;
using Beacon.Tests.Common;

namespace Beacon.Tests.Unit.HostEndpoints;

/// <summary>
/// An in-process host (TestServer) with Web.Admin-style controllers, minimal APIs, claim-based policies and a global
/// antiforgery filter, wired with <c>AddHostEndpointTools</c>. No database: the project resolver is faked and audit
/// rows are captured in memory (§4.7).
/// </summary>
internal sealed class HostEndpointTestHost : IAsyncDisposable
{
    public const int ProjectId = 7;
    public const string ProjectName = "Netgiro";
    public const string PermissionClaim = "permission";
    public const string ViewThingPolicy = "ViewThing";
    public const string ViewLoansPolicy = "ViewLoans";
    public const string ViewThings = "things.view";
    public const string ViewLoans = "loans.view";
    public const string CallerHash = "c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00c0ffee00";

    public static readonly Type[] DefaultControllers = [typeof(ThingsController), typeof(AdminLoansController)];

    private HostEndpointTestHost(WebApplication app, List<McpAuditLog> auditLogs)
    {
        App = app;
        AuditLogs = auditLogs;
    }

    public WebApplication App { get; }

    public List<McpAuditLog> AuditLogs { get; }

    public HostEndpointToolRegistry Registry => App.Services.GetRequiredService<HostEndpointToolRegistry>();

    public static async Task<HostEndpointTestHost> StartAsync(
        Type[]? controllers = null,
        Action<WebApplication>? map = null,
        Action<HostEndpointToolOptions>? configure = null,
        Action<IServiceCollection>? services = null,
        bool mapDefaultMinimalApis = true,
        bool withMcpServer = false,
        Action<WebApplication>? beforeMcp = null,
        bool retainContent = true,
        bool fallbackPolicy = false)
    {
        var auditLogs = new List<McpAuditLog>();
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Testing" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services
            .AddControllersWithViews(x => x.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()))
            .ConfigureApplicationPartManager(x =>
            {
                x.ApplicationParts.Clear();
                x.FeatureProviders.Add(new ExplicitControllerProvider(controllers ?? DefaultControllers));
            });
        builder.Services.AddAntiforgery();
        builder.Services.AddAuthorization(x =>
        {
            x.AddPolicy(ViewThingPolicy, y => y.RequireClaim(PermissionClaim, ViewThings));
            x.AddPolicy(ViewLoansPolicy, y => y.RequireClaim(PermissionClaim, ViewLoans));
            if (fallbackPolicy)
            {
                x.FallbackPolicy = new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder()
                    .RequireClaim(PermissionClaim, ViewThings)
                    .Build();
            }
        });

        var resolver = new Mock<IHostEndpointProjectResolver>();
        resolver
            .Setup(x => x.ResolveAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(ProjectId);
        builder.Services.AddSingleton(resolver.Object);

        var auditFactory = new Mock<IDbContextFactory<BeaconContext>>();
        auditFactory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new AuditCapturingContext(auditLogs));
        builder.Services.AddSingleton(auditFactory.Object);
        builder.Services.AddSingleton(SettingsProviderMock.Create(new McpSettingsData { RetainQueryContent = retainContent }).Object);

        if (withMcpServer)
        {
            Beacon.MCP.ServiceConfiguration.AddBeaconMcp(builder.Services);
        }
        else
        {
            builder.Services.AddScoped<McpProjectContext>();
            builder.Services.AddScoped<IProjectContext>(x => x.GetRequiredService<McpProjectContext>());
            builder.Services.AddTransient<McpAuditService>();
        }

        services?.Invoke(builder.Services);
        builder.Services.AddHostEndpointTools(x =>
        {
            x.ProjectName = ProjectName;
            x.NamedToolLimit = 100;
            configure?.Invoke(x);
        });

        var app = builder.Build();
        beforeMcp?.Invoke(app);
        app.UseRouting();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapControllers();

        if (mapDefaultMinimalApis)
        {
            app.MapGet("/minimal/echo/{name}", (string name, int? count) => Results.Ok(new { name, count }))
                .RequireAuthorization(ViewThingPolicy)
                .WithBeaconTool("minimal_echo", "Echo a name.", readOnly: true);

            app.MapPost("/minimal/form", ([FromForm] string term, [FromForm] int? limit) => Results.Ok(new { term, limit }))
                .RequireAuthorization(ViewThingPolicy)
                .WithBeaconTool("minimal_form", "Search by a form term.", readOnly: true);

            app.MapGet("/minimal/unmarked", () => Results.Ok()).RequireAuthorization(ViewThingPolicy);

            app.MapPost("/minimal/public-form", ([FromForm] string term) => Results.Ok(new { term }))
                .AllowAnonymous()
                .WithBeaconTool("minimal_public_form", "An anonymous form read.", readOnly: true);
        }

        map?.Invoke(app);

        if (withMcpServer)
        {
            app.MapMcp("/beacon/mcp");
        }

        await app.StartAsync();

        return new HostEndpointTestHost(app, auditLogs);
    }

    public HostEndpointToolDescriptor Tool(string name) =>
        Registry.Find(name) ?? throw new InvalidOperationException($"No tool {name}.");

    public static McpCaller SystemCaller(params Claim[] hostClaims) =>
        new(McpCallerKind.System, "routine-runner", CallerHash, null, [ProjectId], McpCallerScope.Execute, hostClaims);

    public static McpCaller UserCaller(IReadOnlyList<int>? projects = null, params Claim[] hostClaims) =>
        new(McpCallerKind.User, "Ada Lovelace", CallerHash, 11, projects ?? [ProjectId], McpCallerScope.Execute, hostClaims);

    /// <summary>The MCP request's principal as <c>JwtBearerAuthMiddleware</c> mints it for a mapped caller.</summary>
    public static ClaimsPrincipal McpPrincipal(McpCaller caller) =>
        new(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, caller.BeaconUserId?.ToString() ?? caller.SubjectHash),
                new Claim(ClaimTypes.Name, caller.Name),
                new Claim(ClaimTypes.Email, "ada@example.com"),
                new Claim("oid", "00000000-0000-0000-0000-00000000000a"),
                new Claim(ClaimTypes.Role, "Analyst"),
                new Claim("roles", "Admin"),
                new Claim("groups", "11111111-2222-3333-4444-555555555555"),
                new Claim(McpCallerClaimTypes.AllowedProjects, JsonSerializer.Serialize(caller.AllowedProjectIds)),
                new Claim(McpCallerClaimTypes.Scope, caller.Scope.ToString()),
                new Claim(McpCallerClaimTypes.CallerKind, caller.Kind.ToString()),
                new Claim(McpCallerClaimTypes.CallerHash, caller.SubjectHash)
            ],
            "Bearer"));

    /// <summary>
    /// The MCP request as the tool service sees it. Carries an Authorization header and a cookie so tests can prove
    /// neither reaches the host endpoint.
    /// </summary>
    public static DefaultHttpContext OuterRequest(McpCaller? caller, ClaimsPrincipal? principal = null)
    {
        var context = new DefaultHttpContext
        {
            User = principal ?? (caller == null ? new ClaimsPrincipal(new ClaimsIdentity([new Claim("auth_method", "api_key"), new Claim("allowed_projects", "[7]")], "ApiKey")) : McpPrincipal(caller))
        };
        context.Request.Headers.Authorization = "Bearer mcp-caller-token";
        context.Request.Headers.Cookie = "ng.admin=browser-session-cookie";
        if (caller != null)
        {
            context.Items[typeof(McpCaller)] = caller;
        }

        return context;
    }

    public HostEndpointToolService CreateService(
        HttpContext outer,
        IReadOnlyList<int>? contextProjects = null,
        IMcpHostPrincipalFactory? principalFactory = null,
        IHostEndpointProjectResolver? projectResolver = null)
    {
        var accessor = new HttpContextAccessor { HttpContext = outer };
        var projectContext = new McpProjectContext { AllowedProjectIds = (contextProjects ?? [ProjectId]).ToList(), UserId = 11 };
        var audit = new McpAuditService(
            App.Services.GetRequiredService<IDbContextFactory<BeaconContext>>(),
            App.Services.GetRequiredService<Beacon.Core.Services.IMcpSettingsProvider>(),
            accessor,
            NullLogger<McpAuditService>.Instance);

        return new HostEndpointToolService(
            Registry,
            App.Services.GetRequiredService<HostEndpointDispatcher>(),
            projectResolver ?? App.Services.GetRequiredService<IHostEndpointProjectResolver>(),
            principalFactory ?? App.Services.GetRequiredService<IMcpHostPrincipalFactory>(),
            projectContext,
            accessor,
            audit,
            App.Services.GetRequiredService<HostEndpointToolOptions>());
    }

    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }

    private sealed class ExplicitControllerProvider(Type[] controllers) : IApplicationFeatureProvider<ControllerFeature>
    {
        public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
        {
            foreach (var controller in controllers)
            {
                feature.Controllers.Add(controller.GetTypeInfo());
            }
        }
    }

    /// <summary>Captures the audit rows the service adds; no database (§4.7).</summary>
    private sealed class AuditCapturingContext : BeaconContext
    {
        private static readonly DbContextOptions<AuditCapturingContext> Options =
            new DbContextOptionsBuilder<AuditCapturingContext>()
                .UseNpgsql("Host=localhost;Database=unused")
                .UseSnakeCaseNamingConvention()
                .Options;

        private readonly Mock<DbSet<McpAuditLog>> _set = new();

        public AuditCapturingContext(List<McpAuditLog> logs) : base(Options, "beacon")
        {
            _set.Setup(x => x.Add(It.IsAny<McpAuditLog>())).Callback<McpAuditLog>(x =>
            {
                lock (logs)
                {
                    logs.Add(x);
                }
            });
        }

        public override DbSet<TEntity> Set<TEntity>() where TEntity : class
        {
            if (typeof(TEntity) == typeof(McpAuditLog))
            {
                return (DbSet<TEntity>)(object)_set.Object;
            }

            return base.Set<TEntity>();
        }

        public override int SaveChanges() => 0;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(0);
    }
}
