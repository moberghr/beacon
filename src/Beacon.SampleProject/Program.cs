using Microsoft.EntityFrameworkCore;
using Warp.Core;
using Warp.Core.Concurrency;
using Warp.Core.Timeout;
using Warp.Worker;
using Warp.Provider.PostgreSql;
using Warp.UI.UIMiddleware;
using Beacon.SampleProject.Warp;
using Beacon.SampleProject.Warp.Jobs;
using Beacon.AI;
using Beacon.Core;
using Beacon.Core.Notifications;
using Beacon.Core.Worker;
using Beacon.Core.PostgreSql;
using Beacon.Core.SqlServer;
using Beacon.Core.Authentication;
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
using Beacon.MCP.Discovery;
using Beacon.Api;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Api.OpenApi;
using Beacon.Api.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Microsoft.AspNetCore.OpenApi;
using Beacon.Api.Authentication;
using Beacon.SampleProject.Authentication;
using Beacon.SampleProject.Middleware;
using Beacon.SampleProject.Services;
using Beacon.UI;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

// Beacon:EncryptionKey comes from user-secrets in Development and from a secret store or the Beacon__EncryptionKey
// environment variable elsewhere — never from a committed file. AddBeaconServices refuses a missing, weak or published key
// in every environment.

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

// OpenTelemetry — opt-in, host-only (§2.4: Beacon libraries never choose an exporter or backend). With no
// OTEL_EXPORTER_OTLP_ENDPOINT configured, nothing below runs and the host behaves exactly as it does today. The
// rest of the standard OTEL_* env vars (OTEL_SERVICE_NAME, OTEL_TRACES_SAMPLER, OTEL_EXPORTER_OTLP_HEADERS, ...)
// drive the SDK directly — no Beacon config for any of it (§0.2/§1.2: no endpoints or headers hardcoded here).
// JSON console logs with trace/span ids for CloudWatch-style log correlation are config only (Logging:Console:
// FormatterName=json, FormatterOptions:IncludeScopes=true) — see docs/site .../mcp-observability-and-audit.md.
if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
{
    builder.Services.AddOpenTelemetry()
        .ConfigureResource(x => x.AddService(serviceName: builder.Configuration["OTEL_SERVICE_NAME"] ?? "beacon"))
        .WithTracing(x => x
            .AddAspNetCoreInstrumentation(y => y.Filter = z => !z.Request.Path.StartsWithSegments("/beacon/api/health"))
            // Notification calls are left out: their URLs carry the recipients' secrets (e.g. a Slack webhook path).
            .AddHttpClientInstrumentation(y => y.FilterHttpRequestMessage = z => !NotificationRequests.IsNotificationRequest(z))
            .AddBeaconInstrumentation()
            .AddOtlpExporter())
        .WithMetrics(x => x
            .AddAspNetCoreInstrumentation()
            .AddHttpClientInstrumentation()
            .AddBeaconInstrumentation()
            .AddOtlpExporter());
}

// Configure Warp (background jobs, recurring jobs, dashboard — replaces Hangfire).
// Warp's schema lives in a dedicated WarpDbContext (the "warp" schema), registered Scoped so
// Warp's publisher/worker services can resolve it and so Warp can decorate
// DbContextOptions<WarpDbContext> with its model customizer + row-lock interceptors. This keeps
// Beacon's own abstract/factory-based BeaconContext untouched.
// Warp only supports PostgreSQL today, so it takes its own connection string ("WarpContext")
// and falls back to BeaconContext when the host runs Beacon on PostgreSQL too. When Beacon is
// pointed at SQL Server, set ConnectionStrings:WarpContext to a PostgreSQL database.
builder.Services.AddDbContext<WarpDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("WarpContext")
                      ?? builder.Configuration.GetConnectionString("BeaconContext"))
           .UseSnakeCaseNamingConvention());

// Cap the worker count explicitly. Each scheduled-query job opens a BeaconContext plus a
// downstream connector connection, so an unbounded worker count would exhaust the Npgsql pool
// (default 100) at scale and — with retries disabled (§6.4) — turn pool-timeout failures into
// terminal job failures.
builder.Services.AddWarpServer<WarpDbContext>(opt =>
{
    opt.UsePostgreSql();

    // §6.4 — no automatic retry; failures are investigated, not retried blindly.
    // (Deliberately no opt.AddRetry().) Polling cadence matches the old Hangfire 1s interval.
    opt.PollingInterval = TimeSpan.FromSeconds(1);
    opt.WorkerCount = Math.Min(Environment.ProcessorCount * 2, 20);

    // Enforce the [Timeout] / [Mutex] attributes on the jobs (Warp/Jobs/*.cs); without these the attributes are inert.
    opt.AddConcurrency();
    opt.AddTimeout();

    // Completed jobs (and their arguments) are kept a week, then cleaned up; failed jobs are never expired.
    opt.JobExpirationTimeout = TimeSpan.FromDays(7);
});

// Host-level identity plumbing (claims transformer; the job status → SignalR bridge is a Warp
// behavior). The SignalR user-id provider comes with AddBeaconApiServices below (§2.12).
builder.Services.AddBeaconHostInfrastructure<SampleClaimsTransformation>();

// ============================================================================
// BEACON SETUP
// ============================================================================

// Step 1: Add Beacon core services with database provider

builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
        options.BaseUrl = builder.Configuration["Beacon:BaseUrl"] ?? "https://localhost:7187/beacon"; // For notification links
        options.UseAI = true;

        options.AddEmailAdapter<BeaconMailSender>();

        // Enable authorization with role-based access control
        options.Authorization.Enabled = true;

        // Enable login form authentication
        options.Authentication.EnableLoginForm = true;
        options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();
        // First-run setup asks for a setup token: Beacon:UserManagement:SetupToken when configured, otherwise the
        // token Beacon prints once to the process console (stderr, not the logger) at startup while no user exists yet.
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
    // The connection string comes from ConnectionStrings:BeaconContext; the schema is always beacon.
    .UsePostgreSql()
    ;

// Step 2: Cookie authentication (React shell at /beacon)
builder.Services.AddBeaconCookieAuthentication(BeaconUiEndpointRouteBuilderExtensions.BasePath);

// Step 2a: Data Protection (protects the session cookie). Beacon's default: the key ring in the Beacon database under
// Beacon:DataProtection:ApplicationName (required outside Development). Outside Development a host must also encrypt the
// keys at rest, or it does not start — chain ProtectKeysWithAzureKeyVault(...) or ProtectKeysWithCertificate(...) here,
// with a key or certificate only that environment holds. A host with its own Data Protection setup skips this call.
builder.Services.AddBeaconDataProtection(builder.Configuration, builder.Environment);

// Step 2b: OIDC authentication (SSO)
builder.Services.AddBeaconOidcAuthentication(builder.Configuration);

// Step 2c: MCP bearer JWT authentication if OIDC is enabled with a JWKS endpoint
var oidc = OidcAuthenticationOptions.FromConfiguration(builder.Configuration);
if (oidc.Enabled && !string.IsNullOrWhiteSpace(oidc.McpJwksEndpoint))
{
    builder.Services.AddBeaconJwtAuthentication(jwt =>
    {
        jwt.EnableBearerAuthentication = true;
        jwt.Validation.JwksEndpoint = oidc.McpJwksEndpoint;
        jwt.Validation.ValidIssuer = oidc.GetEnabledAuthority();
        jwt.Validation.ValidAudience = oidc.ClientId;
        jwt.Validation.ValidateIssuer = true;
        jwt.Validation.ValidateAudience = true;
    });
}

// (Claims transformer supplied to AddBeaconHostInfrastructure above — the shipped helper takes it
// as a type argument rather than hardcoding this sample's implementation.)

// Step 3: Add Beacon AI services (required for AI features)
builder.Services.AddBeaconAI(builder.Configuration);
builder.Services.AddBeaconBedrock();
builder.Services.AddBeaconOnnxEmbeddings();

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

// SignalR + the IUserIdProvider are wired by AddBeaconApiServices() itself; the host adds nothing.
// Turn realtime off with AddBeaconApiServices(x => x.Realtime = false).
builder.Services.AddBeaconApiServices();

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

// Security headers on Beacon's routes (Beacon:Security): before UseStaticFiles so the SPA's files carry them too.
// UseBeaconAuthentication adds them itself when a host does not call this. HSTS is the host's: UseHsts binds the whole host name.
app.UseBeaconSecurityHeaders();

app.UseStaticFiles();

// Convert exceptions thrown inside /beacon/api/* into RFC 7807 problem+json responses.
// Must sit BEFORE the auth/antiforgery/rate-limiter middlewares so their exceptions are
// also translated instead of surfacing as raw 500s.
app.UseApiExceptionHandler("/beacon/api");

// MCP discovery challenge decoration (tier 3): adds the RFC 9728 WWW-Authenticate
// resource_metadata pointer to every 401 from /beacon/mcp. Response-header-only — it never
// authenticates or short-circuits; registered before UseBeaconAuthentication so the OnStarting hook
// also covers the 401s Beacon's authentication writes for a refused key or token. §1.9 order below is untouched.
app.UseMcpResourceMetadataChallenge();

// Middleware order is load-bearing per §1.9:
//   Authentication → Beacon (a presented API key or JWT bearer, else the Beacon.Auth cookie) → Authorization
//   → LoginForm.
// UseBeaconAuthentication wires the first two. A presented key or token wins over any session, so its scope and
// projects apply and MCP clients are never redirected, and the result is recorded as the request's authentication result BEFORE UseAuthorization
// evaluates policies (the first cookie request is authorized). Beacon's authentication acts on /beacon and on
// Beacon:Authentication:AdditionalPaths only (this sample lists /warp in appsettings.json): a Beacon
// credential never authenticates another route of the host.
app.UseBeaconAuthentication();
app.UseAuthorization();

// Login form redirect middleware — redirects unauthenticated browser requests to /login (React route)
var beaconConfiguration = app.Services.GetRequiredService<BeaconConfiguration>();
if (beaconConfiguration.Authentication.EnableLoginForm)
{
    app.UseMiddleware<LoginFormAuthMiddleware>(beaconConfiguration, BeaconUiEndpointRouteBuilderExtensions.BasePath);
}

// First-run setup redirect middleware
if (beaconConfiguration.UserManagement.Enabled)
{
    app.UseMiddleware<FirstRunSetupMiddleware>(beaconConfiguration, BeaconUiEndpointRouteBuilderExtensions.BasePath);
}

// Antiforgery middleware must run after auth so it can issue tokens for the current user.
app.UseAntiforgery();

// OpenAPI document at /openapi/v1.json - consumed by NSwag for React TS codegen. Open in Development; elsewhere it needs
// a signed-in Beacon caller (this sample lists /openapi in Beacon:Authentication:AdditionalPaths) unless
// Beacon:OpenApi:Anonymous is true.
app.MapBeaconOpenApi();

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

// JobStatusChanged push to /beacon/api/hub is handled by JobStatusChangedBehavior, a Warp
// pipeline behavior auto-registered by the Warp source generator — no manual filter wiring.

// Beacon MCP Server - available at /beacon/mcp (Streamable HTTP, SDK transport).
// §1.4 — the MCP tools ask/query execute SQL, so the route requires the same Execute scope
// as the REST SQL endpoints; a Read-scoped API key must not reach SQL execution through MCP.
// Entra JWT callers are scope-gated too: IMcpCallerMapper (Beacon:Mcp:Callers) decides their scope and
// projects. Cookie/OIDC sessions are unaffected (the policy only constrains API keys and mapped MCP JWT callers).
app.MapBeaconMcp();

// MCP discovery documents (tier 3, anonymous by design): RFC 9728 protected-resource metadata at
// /.well-known/oauth-protected-resource (+ /beacon/mcp path-inserted variant) and the SEP-2127
// (draft) server card at /.well-known/mcp/server-card.json. Allow-listed in LoginFormAuthMiddleware.
app.MapMcpDiscovery();

// Warp Dashboard - available at /warp — Admin-only (§7.4).
app.UseGuardedWarpUI(warpUiOptions =>
{
    warpUiOptions.RoutePrefix = "/warp";
    warpUiOptions.Authorization = new WarpDashboardAuthFilter();
});

// React SPA shell at /beacon. Beacon.UI ships the built React app as Razor Class
// Library static web assets; MapBeaconUi() wires the SPA fallback so client-side
// routes resolve to its index.html. Real asset requests fall through to UseStaticFiles.
// The root path is left to the host app — see BeaconUiEndpointRouteBuilderExtensions.
app.MapBeaconUi();

// Warp startup: apply the warp-schema migrations, then register static recurring jobs. The
// WarpInitial migration EnsureSchema's the "warp" schema, so this bootstraps a fresh database;
// it must run before AddOrUpdateRecurringJob (which writes to warp.recurring_job).
// The boot work is bounded by a stop signal and a ceiling, so a stuck database cannot hold the start forever.
// WaitAsync(bootToken) only abandons the wait: the underlying call (which takes no token) may still finish on the scope.
// Migration is safe to cut (idempotent, one transaction per migration).
using (var bootCancellation = new BootCancellation(TimeSpan.FromMinutes(10)))
using (var warpStartupScope = app.Services.CreateScope())
{
    var bootToken = bootCancellation.Token;

    await warpStartupScope.ServiceProvider.GetRequiredService<WarpDbContext>().Database.MigrateAsync(bootToken);

    var recurringJobPublisher = warpStartupScope.ServiceProvider.GetRequiredService<IRecurringJobPublisher>();

    // MCP Learning: aggregate patterns every 6 hours, cleanup old signals daily
    await recurringJobPublisher.AddOrUpdateRecurringJob(new AggregateLearnedPatternsJob(), "mcp-learning-aggregate", "0 */6 * * *").WaitAsync(bootToken);
    await recurringJobPublisher.AddOrUpdateRecurringJob(new CleanupOldSignalsJob(), "mcp-learning-cleanup", "0 3 * * *").WaitAsync(bootToken);

    // MCP Audit: purge rows older than Beacon:Mcp:Audit:RetentionDays daily (no-op unless set). Consumer
    // hosts (e.g. Netgiro) schedule this themselves.
    await recurringJobPublisher.AddOrUpdateRecurringJob(new PurgeExpiredMcpAuditLogsJob(), "mcp-audit-retention", "30 3 * * *").WaitAsync(bootToken);

    // Data retention: delete run history and logs older than Beacon:Retention:Days (0, the default, keeps
    // everything) and clear content older than Beacon:Retention:ContentDays, daily. Consumer hosts schedule this themselves.
    await recurringJobPublisher.AddOrUpdateRecurringJob(new DataRetentionJob(), DataRetentionJob.Name, "50 3 * * *").WaitAsync(bootToken);

    // Security audit: purge events older than Beacon:Audit:RetentionDays daily (no-op while unset: kept forever).
    // Consumer hosts schedule this themselves.
    await recurringJobPublisher.AddOrUpdateRecurringJob(new PurgeExpiredSecurityAuditEventsJob(), "security-audit-retention", "45 3 * * *").WaitAsync(bootToken);

    // Security audit: verify the events written since the last run against their integrity hashes, hourly.
    // Consumer hosts schedule this themselves.
    await recurringJobPublisher.AddOrUpdateRecurringJob(new VerifySecurityAuditEventsJob(), "security-audit-verify", "15 * * * *").WaitAsync(bootToken);

    // MCP Embeddings: re-index metadata + exemplars for hybrid retrieval / semantic few-shot every 12 hours
    await recurringJobPublisher.AddOrUpdateRecurringJob(new ReindexEmbeddingsJob(), "mcp-embedding-reindex", "0 */12 * * *").WaitAsync(bootToken);

    // MCP Doc chunks (Tier-3 ⑨/⑩): re-chunk + (optional contextual blurb) + re-embed project docs every 12 hours
    await recurringJobPublisher.AddOrUpdateRecurringJob(new ReindexDocChunksJob(), "mcp-docchunk-reindex", "0 */12 * * *").WaitAsync(bootToken);

    // EncryptRecipientSecretsJob (Warp/Jobs/NotificationJobs.cs) is deliberately not enqueued here: it rewrites stored
    // recipient secrets, which an older version cannot read, so an operator runs it once every node runs this version
    // (see "Notification Destinations" in the configuration docs).

    // Retired encryption keys: while Beacon:PreviousEncryptionKeys lists a key, every start enqueues one run of the
    // re-encryption job, which moves stored values still under a previous key to Beacon:EncryptionKey and logs how many
    // there were. Once it reports none, remove the previous keys from the configuration.
    if (Beacon.Core.ServiceConfiguration.HasPreviousEncryptionKeys(app.Configuration))
    {
        var publisher = warpStartupScope.ServiceProvider.GetRequiredService<IPublisher>();
        await publisher.Enqueue(new ReEncryptStoredValuesJob());
        await publisher.SaveChangesAsync(bootToken);
    }
}

app.Run();

// Marker partial class for WebApplicationFactory<Program> in Beacon.Tests integration tests.
public partial class Program;
