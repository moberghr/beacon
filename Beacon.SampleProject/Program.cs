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
using Beacon.Api.OpenApi;
using Microsoft.AspNetCore.OpenApi;
using Beacon.Api.SignalR;
using Beacon.SampleProject.Authentication;
using Beacon.SampleProject.Middleware;
using Beacon.SampleProject.Services;
using Beacon.UI;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using System.Net;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// Fail fast if a production deployment is still using the placeholder encryption key shipped in
// appsettings.json for local dev. With that publicly-known key, every secret encrypted at rest
// (connection strings, API keys) would be trivially decryptable.
if (builder.Environment.IsProduction()
    && builder.Configuration["Beacon:EncryptionKey"] == Beacon.Core.ServiceConfiguration.DefaultEncryptionKey)
{
    throw new InvalidOperationException(
        "Beacon:EncryptionKey is set to the built-in placeholder value in Production. " +
        "Generate a unique key (openssl rand -base64 32) and supply it via an environment " +
        "variable or secret store — never the committed appsettings.json default.");
}

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
        x => x.UseNpgsqlConnection(builder.Configuration.GetConnectionString("BeaconContext")),
        new PostgreSqlStorageOptions
        {
            PrepareSchemaIfNecessary = true,
            QueuePollInterval = TimeSpan.FromSeconds(1),
        }));

// Cap the worker count explicitly. The Hangfire default is 20 × ProcessorCount (e.g. 160 on an
// 8-core host); each scheduled-query job opens a BeaconContext plus a downstream connector
// connection, so the default would exhaust the Npgsql pool (default 100) at scale and — with
// retries disabled (Attempts = 0) — turn pool-timeout failures into terminal job failures.
builder.Services.AddHangfireServer(options =>
    options.WorkerCount = Math.Min(Environment.ProcessorCount * 2, 20));

// Host-level identity + SignalR plumbing (claims transformer, Hangfire → SignalR bridge,
// SignalR user-id provider). Registered together via AuthServiceExtensions (§2.12).
builder.Services.AddBeaconHostInfrastructure();

// ============================================================================
// BEACON SETUP
// ============================================================================

// Step 1: Add Beacon core services with database provider

builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
        options.BaseUrl = builder.Configuration["Beacon:BaseUrl"] ?? "https://localhost:7187"; // For notification links
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

// (Claims transformer registered via AddBeaconHostInfrastructure above.)

// Step 3: Add Beacon AI services (required for AI features)
builder.Services.AddBeaconAI(builder.Configuration);

// Step 4: Add Beacon MCP server
builder.Services.AddBeaconMcp();

// Step 5: REST API surface for the React shell.
// OpenAPI document is emitted at /openapi/v1.json (consumed by NSwag for TS codegen).
// Enums stay integers on the wire; the transformer + reference-id hook emit named
// component schemas so the generated TS client carries the real backend enum values.
builder.Services.AddOpenApi(options =>
{
    options.CreateSchemaReferenceId = x =>
    {
        var type = Nullable.GetUnderlyingType(x.Type) ?? x.Type;
        return type.IsEnum ? type.Name : OpenApiOptions.CreateDefaultSchemaReferenceId(x);
    };
    options.AddSchemaTransformer<EnumSchemaTransformer>();
});
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
// (IUserIdProvider registered via AddBeaconHostInfrastructure above.)

var app = builder.Build();

// ============================================================================
// MIDDLEWARE PIPELINE
// ============================================================================

// Behind a reverse proxy (TLS termination, load balancer) the per-IP rate limiter and
// SameAsRequest cookie security key off the proxy's address/scheme unless forwarded
// headers are honored. Opt-in via config so direct-exposed deployments aren't spoofable.
if (app.Configuration.GetValue<bool>("Beacon:ForwardedHeaders:Enabled"))
{
    var forwardedOptions = new ForwardedHeadersOptions
    {
        ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
    };

    // Only trust explicitly configured proxies; an empty list trusts none and the
    // headers are ignored, which fails safe.
    forwardedOptions.KnownProxies.Clear();
    forwardedOptions.KnownNetworks.Clear();
    foreach (var proxy in app.Configuration.GetSection("Beacon:ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
    {
        if (IPAddress.TryParse(proxy, out var address))
        {
            forwardedOptions.KnownProxies.Add(address);
        }
    }

    app.UseForwardedHeaders(forwardedOptions);
}

// In development, skip HTTPS redirect for MCP endpoints so local tools can connect over HTTP.
// In production, HTTPS redirection applies everywhere — including /beacon/mcp, which carries
// API keys in the Authorization header and must never transit plaintext.
if (app.Environment.IsDevelopment())
{
    app.UseWhen(
        context => !context.Request.Path.StartsWithSegments("/beacon/mcp"),
        appBuilder => appBuilder.UseHttpsRedirection());
}
else
{
    app.UseHttpsRedirection();
}

app.UseStaticFiles();

// Convert exceptions thrown inside /beacon/api/* into RFC 7807 problem+json responses.
// Must sit BEFORE the auth/antiforgery/rate-limiter middlewares so their exceptions are
// also translated instead of surfacing as raw 500s.
app.UseApiExceptionHandler("/beacon/api");

// API key auth must run before cookie auth to prevent redirect for MCP clients
app.UseMiddleware<ApiKeyAuthMiddleware>();

// JWT bearer authentication for MCP clients using OIDC tokens
app.UseBeaconJwtBearerAuthentication();

// Middleware order is load-bearing per §1.9:
//   ApiKey → Authentication → Cookie (per-request cookie scheme) → Authorization → LoginForm.
// BeaconCookieAuthMiddleware must populate context.User from the Beacon.Auth cookie BEFORE
// UseAuthorization evaluates policies, otherwise the first authorization check on a cookie
// session sees an unauthenticated user.
app.UseAuthentication();
app.UseMiddleware<BeaconCookieAuthMiddleware>();
app.UseAuthorization();

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

// REST API surface for the React shell. Adds /beacon/api/{health, auth/me, auth/permissions, csrf}.
app.MapBeaconApi();

// Login/logout/sso endpoints (cookie sign-in/out + SSO challenge) at /beacon/api/auth/*.
// Mapped outside the BeaconApi group because the group's RequireAuthorization(AuthPolicyName)
// and antiforgery filter would block login itself; these endpoints configure their own auth.
app.MapLoginEndpoints("/beacon", beaconConfiguration);

// First-run setup endpoints at /beacon/api/setup/*.
// Outside the group: setup must run before any user (and any antiforgery cookie) exists.
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

// Hangfire Dashboard - available at /hangfire — Admin-only (§7.4).
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    IgnoreAntiforgeryToken = true,
    Authorization = [new HangfireDashboardAuthFilter()],
});

// React SPA shell at root /. Beacon.UI ships the built React app as Razor Class
// Library static web assets; MapBeaconUi() wires the SPA fallback so client-side
// routes resolve to its index.html. Real asset requests fall through to UseStaticFiles.
app.MapBeaconUi();

// MCP Learning: aggregate patterns every 6 hours, cleanup old signals daily
RecurringJob.AddOrUpdate<IJobService>("mcp-learning-aggregate", x => x.AggregateLearnedPatterns(JobCancellationToken.Null), "0 */6 * * *");
RecurringJob.AddOrUpdate<IJobService>("mcp-learning-cleanup", x => x.CleanupOldSignals(JobCancellationToken.Null), "0 3 * * *");

app.Run();

// Marker partial class for WebApplicationFactory<Program> in Beacon.Tests integration tests.
public partial class Program;
