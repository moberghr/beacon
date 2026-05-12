using Hangfire;
using Hangfire.PostgreSql;
using Beacon.AI;
using Beacon.Core;
using Beacon.Core.Worker;
using Beacon.Core.PostgreSql;
using Beacon.Core.SqlServer;
using Beacon.Core.Authentication.Providers;
using Beacon.Connector.PostgreSql;
using Beacon.Connector.SqlServer;
using Beacon.Connector.MySql;
using Beacon.Connector.CloudWatch;
using Beacon.Connector.AzureSynapse;
using Beacon.Connector.Snowflake;
using Beacon.Connector.Databricks;
using Beacon.Connector.Api;
using Beacon.Connector.BigQuery;
using Beacon.MCP;
using Beacon.Api.Endpoints;
using Beacon.Api.Hubs;
using Beacon.Api.SignalR;
using Beacon.SampleProject.Authentication;
using Beacon.SampleProject.Middleware;
using Beacon.SampleProject.Services;
using Beacon.UI;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.SignalR;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel server limits for long-running AI operations
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
    serverOptions.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);
});

// Configure HTTP clients with extended timeouts for AI operations
builder.Services.AddHttpClient().ConfigureHttpClientDefaults(http =>
{
    http.ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromMinutes(5);
    });
});

// Configure Hangfire with PostgreSQL
builder.Services.AddHangfire(hangfireConfiguration => hangfireConfiguration
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseFilter(new AutomaticRetryAttribute { Attempts = 0 })
    .UsePostgreSqlStorage(
        builder.Configuration.GetConnectionString("BeaconContext"),
        new PostgreSqlStorageOptions
        {
            PrepareSchemaIfNecessary = true,
            QueuePollInterval = TimeSpan.FromSeconds(1),
        }));

builder.Services.AddHangfireServer();

// Hangfire filter that publishes job-state changes via SignalR to the enqueueing user.
builder.Services.AddSingleton<HangfireSignalRJobFilter>();

// ============================================================================
// BEACON SETUP
// ============================================================================

// Step 1: Add Beacon core services with database provider

builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
        options.BaseUrl = "https://localhost:7187"; // For notification links
        options.UseAI = true;

        options.AddEmailAdapter<BeaconMailSender>();

        // Enable authorization with role-based access control
        options.Authorization.Enabled = true;

        // Enable login form authentication
        options.Authentication.EnableLoginForm = true;
        options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();
        options.UserManagement = new UserManagementOptions
        {
            // For demo purposes, allow user registration. In production, you would likely disable this.
            Enabled = true
        };
    })
    // Register data source connectors (enables each engine type)
    .AddPostgreSqlConnector()
    .AddSqlServerConnector()
    .AddMySqlConnector()
    .AddCloudWatchConnector()
    .AddAzureSynapseConnector()
    .AddSnowflakeConnector()
    .AddDatabricksConnector()
    .AddBigQueryConnector()
    .AddApiConnector()
    // Configure EF Core database provider for Beacon's own data store
    .UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!, "semantico")
    ;

// Step 2: Cookie authentication (React shell at root)
builder.Services.AddBeaconCookieAuthentication("/");

// Step 2b: OIDC authentication (SSO)
builder.Services.AddBeaconOidcAuthentication(builder.Configuration);

// Step 2c: MCP bearer JWT authentication if OIDC is enabled with a JWKS endpoint
var oidcSection = builder.Configuration.GetSection("Beacon:Authentication:Oidc");
var oidcEnabled = oidcSection.GetValue<bool>("Enabled");
var mcpJwksEndpoint = oidcSection.GetValue<string>("McpJwksEndpoint");
if (oidcEnabled && !string.IsNullOrWhiteSpace(mcpJwksEndpoint))
{
    var oidcAuthority = oidcSection.GetValue<string>("Authority")?.TrimEnd('/');
    var oidcClientId = oidcSection.GetValue<string>("ClientId");
    builder.Services.AddBeaconJwtAuthentication(jwt =>
    {
        jwt.EnableBearerAuthentication = true;
        jwt.Validation.JwksEndpoint = mcpJwksEndpoint;
        jwt.Validation.ValidIssuer = oidcAuthority;
        jwt.Validation.ValidAudience = oidcClientId;
        jwt.Validation.ValidateIssuer = true;
        jwt.Validation.ValidateAudience = !string.IsNullOrEmpty(oidcClientId);
    });
}

// Register claims transformer to add role claims after authentication
builder.Services.AddScoped<Microsoft.AspNetCore.Authentication.IClaimsTransformation, SampleClaimsTransformation>();

// Step 3: Add Beacon AI services (required for AI features)
builder.Services.AddBeaconAI(builder.Configuration);

// Step 4: Add Beacon MCP server
builder.Services.AddBeaconMcp();

// Step 5: REST API surface for the React shell.
// OpenAPI document is emitted at /openapi/v1.json (consumed by NSwag for TS codegen).
builder.Services.AddOpenApi();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-XSRF-TOKEN";
    options.Cookie.Name = ".Beacon.Antiforgery";
});
builder.Services.AddBeaconApiAuthorization();

// Rate limiting for sensitive anonymous endpoints (login). 10 requests / 60 seconds
// per remote-IP partition; clients beyond the window get 429 Too Many Requests.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", httpContext =>
    {
        var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(remoteIp, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = 10,
            Window = TimeSpan.FromSeconds(60),
            QueueLimit = 0,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true,
        });
    });
});

builder.Services.AddSignalR();
builder.Services.AddSingleton<IUserIdProvider, HubUserIdProvider>();

var app = builder.Build();

// ============================================================================
// MIDDLEWARE PIPELINE
// ============================================================================

// Skip HTTPS redirect for MCP endpoints (allows local dev tools to connect over HTTP)
app.UseWhen(
    context => !context.Request.Path.StartsWithSegments("/beacon/mcp"),
    appBuilder => appBuilder.UseHttpsRedirection());

app.UseStaticFiles();

// API key auth must run before cookie auth to prevent redirect for MCP clients
app.UseMiddleware<ApiKeyAuthMiddleware>();

// JWT bearer authentication for MCP clients using OIDC tokens
app.UseBeaconJwtBearerAuthentication();

// Authentication must be before authorization
app.UseAuthentication();
app.UseAuthorization();

// Cookie authentication middleware (per-request cookie scheme authentication)
app.UseMiddleware<BeaconCookieAuthMiddleware>();

// Login form redirect middleware — redirects unauthenticated browser requests to /login (React route)
var beaconConfiguration = app.Services.GetRequiredService<BeaconConfiguration>();
if (beaconConfiguration.Authentication.EnableLoginForm)
{
    app.UseMiddleware<LoginFormAuthMiddleware>(beaconConfiguration, "/");
}

// First-run setup redirect middleware
if (beaconConfiguration.UserManagement.Enabled)
{
    app.UseMiddleware<FirstRunSetupMiddleware>(beaconConfiguration, "/");
}

// Antiforgery middleware must run after auth so it can issue tokens for the current user.
app.UseAntiforgery();

// Rate limiter — sits after auth/antiforgery so endpoint-level policies (e.g. "login") apply.
app.UseRateLimiter();

// OpenAPI document at /openapi/v1.json - consumed by NSwag for React TS codegen.
app.MapOpenApi();

// Convert exceptions thrown inside /beacon/api/* into RFC 7807 problem+json responses.
app.UseApiExceptionHandler("/beacon/api");

// REST API surface for the React shell. Adds /beacon/api/{health, auth/me, auth/permissions, csrf}.
app.MapBeaconApi();

// Login/logout/sso endpoints (cookie sign-in/out + SSO challenge) at /beacon/api/auth/*.
app.MapLoginEndpoints("/beacon", beaconConfiguration);

// First-run setup endpoints at /beacon/api/setup/*.
if (beaconConfiguration.UserManagement.Enabled)
{
    app.MapSetupEndpoints("/beacon");
}

// SignalR hub for the React shell. Auth required (cookie scheme).
app.MapHub<BeaconHub>("/beacon/api/hub").RequireAuthorization(BeaconApiEndpoints.AuthPolicyName);

// Register the Hangfire SignalR filter against the global JobStorage, now that DI is built.
Hangfire.GlobalJobFilters.Filters.Add(app.Services.GetRequiredService<HangfireSignalRJobFilter>());

// Beacon MCP Server - available at /beacon/mcp (Streamable HTTP, SDK transport)
app.MapMcp("/beacon/mcp").RequireAuthorization();

// Hangfire Dashboard - available at /hangfire
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    IgnoreAntiforgeryToken = true,
});

// React SPA shell at root /. Beacon.UI ships the built React app as Razor Class
// Library static web assets; MapBeaconUi() wires the SPA fallback so client-side
// routes resolve to its index.html. Real asset requests fall through to UseStaticFiles.
app.MapBeaconUi();

// MCP Learning: aggregate patterns every 6 hours, cleanup old signals daily
RecurringJob.AddOrUpdate<IJobService>("mcp-learning-aggregate", x => x.AggregateLearnedPatterns(), "0 */6 * * *");
RecurringJob.AddOrUpdate<IJobService>("mcp-learning-cleanup", x => x.CleanupOldSignals(), "0 3 * * *");

app.Run();

// Marker partial class for WebApplicationFactory<Program> in Beacon.Tests integration tests.
public partial class Program;
