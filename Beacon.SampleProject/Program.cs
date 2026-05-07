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
using Beacon.SampleProject.Endpoints;
using Beacon.SampleProject.Hubs;
using Beacon.SampleProject.Middleware;
using Beacon.SampleProject.Services;
using Beacon.SampleProject.SignalR;
using Microsoft.AspNetCore.SignalR;
using Beacon.UI;
using Beacon.UI.Authentication;

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
// Jobs without a BeaconUserId parameter publish nothing — see HangfireSignalRJobFilter.
builder.Services.AddSingleton<HangfireSignalRJobFilter>();

// ============================================================================
// BEACON SETUP
// ============================================================================

// Step 1: Add Beacon core services with database provider

builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
        options.BaseUrl = "https://localhost:7187/beacon"; // For notification links
        options.UseAI = true; // Enable AI features (requires LLM configuration)

        options.AddEmailAdapter<BeaconMailSender>();

        // Enable authorization with role-based access control
        options.Authorization.Enabled = true;
        // DatabaseAuthorizationProvider auto-registers when UserManagement is enabled (via TryAddScoped)

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
    //.UseSqlServer(builder.Configuration.GetConnectionString("BeaconContextSql")!, "beacon")
    ;

// Alternative: Use SQL Server instead of PostgreSQL
// builder.Services.AddBeaconWithSqlServer(
//     builder.Configuration,
//     builder.Configuration.GetConnectionString("BeaconContextSql")!,
//     "beacon",
//     options =>
//     {
//         options.AddBeaconScheduler<BeaconScheduler>();
//         options.BaseUrl = "https://localhost:7187/beacon";
//         options.UseAI = true;
//     });

// Step 2: Add Beacon UI components (Blazor + MudBlazor)
builder.Services.AddBeaconUI();

// Step 3: Add cookie authentication for login form
builder.Services.AddBeaconCookieAuthentication("/beacon");

// Step 3b: Add OIDC authentication (SSO)
builder.Services.AddBeaconOidcAuthentication(builder.Configuration);

// Step 3c: Add MCP bearer JWT authentication if OIDC is enabled with a JWKS endpoint
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

// Step 4: Add Beacon AI services (required for AI features)
builder.Services.AddBeaconAI(builder.Configuration);

// Step 5: Add Beacon MCP server (exposes data to AI tools via SSE)
builder.Services.AddBeaconMcp();

// Step 6: REST API surface for the React shell at /beacon/app
// OpenAPI document is emitted at /openapi/v1.json (consumed by NSwag for TS codegen).
builder.Services.AddOpenApi();
builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-XSRF-TOKEN";
    options.Cookie.Name = ".Beacon.Antiforgery";
});
builder.Services.AddBeaconApiAuthorization();
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

// IMPORTANT: UseStaticFiles must be called before UseBeaconUI
// to serve _content files from Razor Class Libraries
app.UseStaticFiles();

// API key auth must run before cookie auth to prevent redirect for MCP clients
app.UseMiddleware<ApiKeyAuthMiddleware>();

// JWT bearer authentication for MCP clients using OIDC tokens
app.UseBeaconJwtBearerAuthentication();

// Authentication must be before authorization
app.UseAuthentication();
app.UseAuthorization();

// Antiforgery middleware must run after auth so it can issue tokens for the current user.
app.UseAntiforgery();

// OpenAPI document at /openapi/v1.json - consumed by NSwag for React TS codegen.
app.MapOpenApi();

// Convert exceptions thrown inside /beacon/api/* into RFC 7807 problem+json responses.
// Scoped via UseWhen so Blazor and MCP keep their existing error semantics.
app.UseApiExceptionHandler("/beacon/api");

// REST API surface for the React shell. Adds /beacon/api/{health, auth/me, auth/permissions, csrf}.
app.MapBeaconApi();

// SignalR hub for the React shell. Auth required (cookie scheme).
app.MapHub<BeaconHub>("/beacon/api/hub").RequireAuthorization(BeaconApiEndpoints.AuthPolicyName);

// Register the Hangfire SignalR filter against the global JobStorage, now that DI is built.
Hangfire.GlobalJobFilters.Filters.Add(app.Services.GetRequiredService<HangfireSignalRJobFilter>());

// Beacon MCP Server - available at /beacon/mcp (Streamable HTTP, SDK transport)
// AI tools like Claude Code connect here via API key authentication
app.MapMcp("/beacon/mcp").RequireAuthorization();

// Beacon Admin UI - available at /beacon
// Using login form instead of basic authentication
app.UseBeaconUI()
    .UseLoginForm() // Login form for demo (use admin/admin, editor/editor, or viewer/viewer)
    .UseAuthorization() // Enable authorization checks
    .AddBlazorUI("/beacon");

// Hangfire Dashboard - available at /hangfire
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    IgnoreAntiforgeryToken = true,
});

// React SPA shell at /app/* - falls back to index.html for client-side routes.
// Mounted at root /app rather than /beacon/app to avoid Blazor's /beacon catch-all.
// Constraint: only paths that look like client routes (no file extension); paths
// with extensions (assets/foo.js, foo.css) fall through to UseStaticFiles.
app.MapFallbackToFile("/app", "app/index.html");
app.MapFallbackToFile("/app/{**path:regex(^([^.]*|.*/[^./]*)$)}", "app/index.html");

// MCP Learning: aggregate patterns every 6 hours, cleanup old signals daily
RecurringJob.AddOrUpdate<IJobService>("mcp-learning-aggregate", x => x.AggregateLearnedPatterns(), "0 */6 * * *");
RecurringJob.AddOrUpdate<IJobService>("mcp-learning-cleanup", x => x.CleanupOldSignals(), "0 3 * * *");

app.Run();

// Marker partial class for WebApplicationFactory<Program> in Beacon.Tests integration tests.
public partial class Program;