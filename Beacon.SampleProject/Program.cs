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
using Beacon.SampleProject.Services;
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
                Enabled =  true
            
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
    .UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon")
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

// MCP Learning: aggregate patterns every 6 hours, cleanup old signals daily
RecurringJob.AddOrUpdate<IJobService>("mcp-learning-aggregate", x => x.AggregateLearnedPatterns(), "0 */6 * * *");
RecurringJob.AddOrUpdate<IJobService>("mcp-learning-cleanup", x => x.CleanupOldSignals(), "0 3 * * *");

app.Run();