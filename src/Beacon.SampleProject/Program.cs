using Microsoft.EntityFrameworkCore;
using Warp.Core;
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
using Beacon.Api;
using Beacon.Api.OpenApi;
using Beacon.Api.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Beacon.Api.Authentication;
using Beacon.Gateway;
using Beacon.SampleProject.Authentication;
using Beacon.SampleProject.Services;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;

var builder = WebApplication.CreateBuilder(args);

// Beacon:EncryptionKey comes from user-secrets in Development and from a secret store or the Beacon__EncryptionKey
// environment variable elsewhere — never from a committed file. AddBeacon refuses a missing, weak or published key
// in every environment.

// Kestrel and HttpClient timeouts of 5 minutes, for long-running AI operations (§6.3).
builder.ConfigureBeaconTimeouts();

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
});

// ============================================================================
// BEACON SETUP
// ============================================================================

builder.Services.AddBeacon(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
        options.BaseUrl = builder.Configuration["Beacon:BaseUrl"] ?? "https://localhost:7187/beacon"; // For notification links

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
    // AI services (switches AI on), then the MCP server at /beacon/mcp.
    .AddAI()
    .AddMcp()
    // REST API for the React shell, its policies and antiforgery (X-XSRF-TOKEN header).
    .AddApi()
    // Beacon.Auth cookie sign-in at /beacon, with the claims transformer and the SignalR user-id provider.
    .AddCookieAuth<SampleClaimsTransformation>()
    // OIDC (SSO) from Beacon:Authentication:Oidc and, when it names a McpJwksEndpoint, bearer JWTs for MCP clients.
    .AddOidc()
    // Beacon's own data store: ConnectionStrings:BeaconContext. Ends the chain.
    .UsePostgreSql()
    // LLM and embedding providers.
    .AddBeaconBedrock()
    .AddBeaconOnnxEmbeddings()
    // OpenAPI document at /openapi/v1.json (consumed by NSwag for the React client), with named enum schemas.
    .AddBeaconOpenApi();

// Data Protection (protects the session cookie). Beacon's default: the key ring in the Beacon database under
// Beacon:DataProtection:ApplicationName (required outside Development). Outside Development a host must also encrypt the
// keys at rest, or it does not start — chain ProtectKeysWithAzureKeyVault(...) or ProtectKeysWithCertificate(...) here,
// with a key or certificate only that environment holds. A host with its own Data Protection setup skips this call.
builder.Services.AddBeaconDataProtection(builder.Configuration, builder.Environment);

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

// Beacon's middlewares, in their load-bearing order (§1.9): RFC 7807 problem responses under /beacon/api, the MCP
// challenge pointer, UseBeaconAuthentication (API key → JWT bearer → UseAuthentication → Beacon.Auth cookie),
// UseAuthorization, the login-form and first-run setup redirects, antiforgery. They act on /beacon and on
// Beacon:Authentication:AdditionalPaths only (this sample lists /warp in appsettings.json): a Beacon credential never
// authenticates another route of the host.
app.UseBeaconPipeline();

// OpenAPI document at /openapi/v1.json - consumed by NSwag for React TS codegen.
app.MapOpenApi();

// Beacon's endpoints: the REST API, sign-in and first-run setup at /beacon/api/*, the MCP server at /beacon/mcp under
// the Execute-scope policy (§1.4), the anonymous MCP discovery documents and the React shell at /beacon.
app.MapBeacon();

// Warp Dashboard - available at /warp — Admin-only (§7.4).
app.UseWarpUI(warpUiOptions =>
{
    warpUiOptions.RoutePrefix = "/warp";
    warpUiOptions.Authorization = new WarpDashboardAuthFilter();
});

// Warp startup: apply the warp-schema migrations, then register static recurring jobs. The
// WarpInitial migration EnsureSchema's the "warp" schema, so this bootstraps a fresh database;
// it must run before AddOrUpdateRecurringJob (which writes to warp.recurring_job).
using (var warpStartupScope = app.Services.CreateScope())
{
    warpStartupScope.ServiceProvider.GetRequiredService<WarpDbContext>().Database.Migrate();

    var recurringJobPublisher = warpStartupScope.ServiceProvider.GetRequiredService<IRecurringJobPublisher>();

    // MCP Learning: aggregate patterns every 6 hours, cleanup old signals daily
    await recurringJobPublisher.AddOrUpdateRecurringJob(new AggregateLearnedPatternsJob(), "mcp-learning-aggregate", "0 */6 * * *");
    await recurringJobPublisher.AddOrUpdateRecurringJob(new CleanupOldSignalsJob(), "mcp-learning-cleanup", "0 3 * * *");

    // MCP Audit: purge rows older than Beacon:Mcp:Audit:RetentionDays daily (no-op when unset). Consumer
    // hosts (e.g. Netgiro) schedule this themselves.
    await recurringJobPublisher.AddOrUpdateRecurringJob(new PurgeExpiredMcpAuditLogsJob(), "mcp-audit-retention", "30 3 * * *");

    // Security audit: purge events older than Beacon:Audit:RetentionDays daily (no-op while unset: kept forever).
    // Consumer hosts schedule this themselves.
    await recurringJobPublisher.AddOrUpdateRecurringJob(new PurgeExpiredSecurityAuditEventsJob(), "security-audit-retention", "45 3 * * *");

    // Security audit: verify the events written since the last run against their integrity hashes, hourly.
    // Consumer hosts schedule this themselves.
    await recurringJobPublisher.AddOrUpdateRecurringJob(new VerifySecurityAuditEventsJob(), "security-audit-verify", "15 * * * *");

    // MCP Embeddings: re-index metadata + exemplars for hybrid retrieval / semantic few-shot every 12 hours
    await recurringJobPublisher.AddOrUpdateRecurringJob(new ReindexEmbeddingsJob(), "mcp-embedding-reindex", "0 */12 * * *");

    // MCP Doc chunks (Tier-3 ⑨/⑩): re-chunk + (optional contextual blurb) + re-embed project docs every 12 hours
    await recurringJobPublisher.AddOrUpdateRecurringJob(new ReindexDocChunksJob(), "mcp-docchunk-reindex", "0 */12 * * *");

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
        await publisher.SaveChangesAsync();
    }
}

app.Run();

// Marker partial class for WebApplicationFactory<Program> in Beacon.Tests integration tests.
public partial class Program;
