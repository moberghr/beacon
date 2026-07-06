---
layout: default
title: Installation
parent: Getting Started
nav_order: 1
---

# Installation Guide

Beacon ships two ways. Pick the path that fits you:

- **[Path A — Run the self-hostable application](#path-a--run-the-self-hostable-application)**: clone the repo and run `Beacon.SampleProject`. Fastest way to evaluate Beacon and the recommended path for contributors.
- **[Path B — Embed Beacon as NuGet packages](#path-b--embed-beacon-as-nuget-packages)**: reference the `Beacon.*` packages and wire them into your own ASP.NET Core app.

Both paths use the same configuration model (see the [Configuration Guide](configuration)).

## Prerequisites

Before you begin, ensure you have:

- **.NET 9.0 SDK** or later
- **PostgreSQL 12+** or **SQL Server 2019+** for Beacon's metadata database
- **Node.js 18+** and npm — only needed to build or run the React frontend from source
- **Visual Studio 2022**, **Rider**, or **VS Code** with C# support
- An **encryption key** (`Beacon:EncryptionKey`) — required (see [Step 2](#step-2-generate-the-encryption-key))

---

## Path A — Run the Self-Hostable Application

The `Beacon.SampleProject` host is the composition root. It self-hosts Kestrel, serves the React SPA at the root URL `/`, exposes the REST API / MCP server / SignalR hub, and includes a working scheduler implementation.

### A1. Clone the repository

```bash
git clone https://github.com/MiBu/semantico.git
cd semantico
```

### A2. Configure secrets

Set the metadata connection string and the required encryption key. Use User Secrets (recommended) or `appsettings.Development.json`:

```bash
dotnet user-secrets --project Beacon.SampleProject set \
  "ConnectionStrings:BeaconContext" \
  "Host=localhost;Database=beacon;Username=postgres;Password=yourpassword"

dotnet user-secrets --project Beacon.SampleProject set \
  "Beacon:EncryptionKey" "$(openssl rand -base64 32)"
```

### A3. Run the API host

```bash
dotnet run --project Beacon.SampleProject --no-launch-profile
```

This starts Kestrel on:

- **HTTP**: http://localhost:5296
- **HTTPS**: https://localhost:7187

On first run Beacon automatically applies EF Core migrations and creates the `beacon` schema. Verify it's up with the health check:

```bash
curl http://localhost:5296/beacon/api/health
```

### A4. (Optional) Run the React dev server

The host serves the pre-built SPA out of `Beacon.UI/wwwroot`. If you're working on the frontend, run the Vite dev server instead — it hot-reloads and proxies API/MCP calls to Kestrel:

```bash
npm install --prefix Beacon.UI/web
npm run dev --prefix Beacon.UI/web
```

Vite serves the app on **http://localhost:5173** and proxies `/beacon/api` and `/beacon/mcp` to Kestrel (port 5296 / 7187), so keep the API host from A3 running alongside it.

Other frontend commands (run inside `Beacon.UI/web`):

| Command | What it does |
|---|---|
| `npm run build` | Production build → outputs to `Beacon.UI/wwwroot` |
| `npm run codegen` | Regenerates the typed TS fetch client from `/openapi/v1.json` via NSwag |
| `npm test` | Runs the Vitest test suite |

### A5. Open the app

| URL | What |
|---|---|
| **`/`** (e.g. https://localhost:7187/) | Beacon React SPA |
| **`/login`** | Login form |
| **`/beacon/api/health`** | API health check |
| **`/openapi/v1.json`** | OpenAPI document |
| **`/beacon/mcp`** | MCP server (auth required) |

On the **first run** Beacon walks you through a setup flow that creates the initial admin user. There are **no hardcoded credentials** — you set them during setup.

---

## Path B — Embed Beacon as NuGet Packages

Embed Beacon into your own ASP.NET Core app by referencing the `Beacon.*` packages.

### B1. Install NuGet packages

```bash
# Core + metadata DB provider (choose one provider)
dotnet add package Beacon.Core
dotnet add package Beacon.Core.PostgreSql   # or: Beacon.Core.SqlServer

# UI (React SPA shipped as a Razor Class Library), REST API, AI, MCP
dotnet add package Beacon.UI
dotnet add package Beacon.Api
dotnet add package Beacon.AI
dotnet add package Beacon.MCP

# Data-source connectors — add only the ones you need
dotnet add package Beacon.Connector.PostgreSql
dotnet add package Beacon.Connector.SqlServer
dotnet add package Beacon.Connector.MySql
dotnet add package Beacon.Connector.BigQuery
dotnet add package Beacon.Connector.Snowflake
dotnet add package Beacon.Connector.Databricks
dotnet add package Beacon.Connector.AzureSynapse
dotnet add package Beacon.Connector.CloudWatch
dotnet add package Beacon.Connector.Api
```

You will also need a job runner for scheduled work — see [B5](#b5-provide-a-scheduler-implementation).

{: .note }
> The `Beacon.Core.PostgreSql` / `Beacon.Core.SqlServer` provider you choose is for Beacon's **metadata** database. The data sources you **monitor** are wired separately via the connector packages, and you can mix any of the nine connectors regardless of which metadata provider you use.

### B2. Generate the encryption key

See [Step 2](#step-2-generate-the-encryption-key) below.

### B3. Configure `appsettings.json`

```json
{
  "ConnectionStrings": {
    "BeaconContext": "Host=localhost;Database=beacon;Username=postgres;Password=yourpassword"
  },
  "Beacon": {
    "EncryptionKey": "k8Jt2mVq9Xw4Zr7yLp3nB6hTsE1dCaG5uFoQiRxYjMA="
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database": "Warning"
    }
  }
}
```

See the [Configuration Guide](configuration) for AI/LLM, OIDC, email, and scheduling options.

### B4. Wire up `Program.cs`

This is the full host setup, modeled on `Beacon.SampleProject/Program.cs`:

```csharp
using Beacon.AI;
using Beacon.Api;
using Beacon.Core;
using Beacon.Core.PostgreSql;
using Beacon.MCP;
using Beacon.UI;

var builder = WebApplication.CreateBuilder(args);

// 1. Your job runner (see B5) — register it here, e.g. Moberg Warp's AddWarpWorker(...)

// 2. Host identity + SignalR plumbing
builder.Services.AddBeaconHostInfrastructure();

// 3. Core services, scheduler, connectors, metadata provider
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();   // your IBeaconScheduler (see B5)
        options.BaseUrl = "https://localhost:7187";
        options.UseAI = true;
        options.AddEmailAdapter<BeaconMailSender>();      // your IEmailAdapter impl

        // Auth + user management
        options.Authorization.Enabled = true;
        options.Authentication.EnableLoginForm = true;
        options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();
        options.UserManagement = new UserManagementOptions { Enabled = true };
    })
    .AddPostgreSqlConnector()
    .AddSqlServerConnector()
    .AddMySqlConnector()
    .AddCloudWatchConnector()
    .AddAzureSynapseConnector()
    .AddSnowflakeConnector()
    .AddDatabricksConnector()
    .AddBigQueryConnector()
    .AddApiConnector()
    .UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");

// 4. Authentication, AI, MCP, OpenAPI
builder.Services.AddBeaconCookieAuthentication("/");          // login redirect target
builder.Services.AddBeaconOidcAuthentication(builder.Configuration); // optional SSO
builder.Services.AddBeaconAI(builder.Configuration);
builder.Services.AddBeaconMcp();
builder.Services.AddOpenApi();

var app = builder.Build();

// 5. Middleware order is load-bearing
app.UseStaticFiles();
app.UseMiddleware<ApiKeyAuthMiddleware>();
app.UseAuthentication();
app.UseMiddleware<BeaconCookieAuthMiddleware>();
app.UseAuthorization();
app.UseAntiforgery();

// 6. Endpoints
app.MapOpenApi();                 // /openapi/v1.json
app.MapBeaconApi();               // /beacon/api/*
app.MapLoginEndpoints("/beacon", beaconConfiguration);
app.MapHub<BeaconHub>("/beacon/api/hub").RequireAuthorization();
app.MapMcp("/beacon/mcp").RequireAuthorization();
app.MapBeaconUi();                // React SPA at root /

app.Run();
```

{: .note }
> **Middleware order matters.** `ApiKeyAuthMiddleware` runs before `UseAuthentication`, and `BeaconCookieAuthMiddleware` runs after it. Reordering breaks API-key-only callers and the login redirect.

### For SQL Server

If you use SQL Server for Beacon's metadata instead of PostgreSQL:

```csharp
using Beacon.Core.SqlServer;

// Metadata provider
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
    })
    // ... connectors ...
    .UseSqlServer(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");
```

Update the connection string in `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "BeaconContext": "Server=localhost;Database=beacon;User Id=sa;Password=YourPassword123!;TrustServerCertificate=True"
  }
}
```

### B5. Provide a scheduler implementation

Beacon does not bundle a job runner — it schedules work through the `IBeaconScheduler` abstraction, so it plugs into whatever your host already uses. Beacon calls `AddOrUpdate` when a subscription is created or its cron changes, and `Remove` when it's deleted or disabled; your implementation maps those calls onto recurring jobs that invoke `IJobService.ExecuteQuery(subscriptionId)`.

```csharp
using Beacon.Core.Worker;

namespace YourProject.Services;

public class BeaconScheduler : IBeaconScheduler
{
    public void AddOrUpdate(int subscriptionId, string subscriptionName, string cron)
    {
        var jobKey = $"{subscriptionId} - {subscriptionName}";
        // register/update a recurring job in your job runner that calls
        // IJobService.ExecuteQuery(subscriptionId) on the given cron schedule
    }

    public void Remove(int subscriptionId, string subscriptionName)
    {
        var jobKey = $"{subscriptionId} - {subscriptionName}";
        // remove the recurring job
    }
}
```

Any job runner with cron/recurring support works. We recommend [Moberg Warp](https://moberghr.github.io/warp/): define an `IJob` that calls `IJobService.ExecuteQuery`, and map `AddOrUpdate`/`Remove` onto Warp's recurring-job APIs — you get retries, concurrency guards (`[Mutex]`), and a job dashboard out of the box. Quartz.NET is another valid choice, and `Beacon.SampleProject` ships a complete working reference implementation you can copy as a starting point.

### B6. (Optional) Extended timeouts for AI operations

AI calls can run for minutes. Kestrel keep-alive and request-header timeouts and the default `HttpClient` timeout are tuned to 5 minutes:

```csharp
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
    serverOptions.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);
});

builder.Services.AddHttpClient().ConfigureHttpClientDefaults(http =>
{
    http.ConfigureHttpClient(client =>
    {
        client.Timeout = TimeSpan.FromMinutes(5);
    });
});
```

### B7. Run

```bash
dotnet run
```

On first run Beacon applies EF Core migrations, creates the `beacon` schema, and walks you through the first-run setup flow that creates the initial admin user. Then open:

| URL | What |
|---|---|
| **`/`** | Beacon React SPA |
| **`/login`** | Login form |

---

## Step 2: Generate the Encryption Key

Beacon **requires** an encryption key (`Beacon:EncryptionKey`) to encrypt sensitive data — most importantly data-source connection strings — at rest with AES-256.

```bash
openssl rand -base64 32
```

**Example output:**

```
k8Jt2mVq9Xw4Zr7yLp3nB6hTsE1dCaG5uFoQiRxYjMA=
```

Store it via User Secrets, an environment variable, or a secrets manager — never commit it. See the [Configuration Guide](configuration) for production patterns.

---

## Authentication

Beacon authentication is **cookie-based** (the `Beacon.Auth` cookie is `HttpOnly`, `SameSite=Lax`) and driven by a pluggable `IBeaconAuthenticationProvider`. The sample uses `DatabaseAuthenticationProvider` (internal users with passwords stored in Beacon). There is **no basic auth and no `admin`/`admin` default** — the first-run setup flow creates the initial admin user.

Beacon supports:

- **Login form** — React `/login` route, backed by the cookie scheme
- **OIDC / SSO** — optional, via `AddBeaconOidcAuthentication(...)`
- **JWT bearer** — for MCP clients
- **API keys** — SHA256-hashed at rest, carry scopes (`Read`, `Execute`, `Admin`) and optional project restrictions; the raw key is shown once at creation

See the [Configuration Guide](configuration#authentication) and the [User Management Guide](../features/user-management) for details.

---

## Troubleshooting

### "Beacon:EncryptionKey must be configured"

Generate and configure the key:

```bash
openssl rand -base64 32
```

### "Cannot create database schema"

Ensure the Beacon database user has permission to create a schema.

**PostgreSQL:**

```sql
GRANT CREATE ON DATABASE beacon TO your_user;
```

**SQL Server:**

```sql
GRANT CREATE SCHEMA TO your_user;
```

### Jobs not executing

1. Verify your job runner is registered and its worker is running.
2. Check your scheduler's dashboard or logs for job status.
3. Confirm the database or storage your job runner uses is reachable.
4. Confirm your `IBeaconScheduler` implementation is registered via `options.AddBeaconScheduler<...>()`.

### SPA not loading at `/`

1. Confirm `app.MapBeaconUi()` is wired and `app.UseStaticFiles()` runs before it.
2. If developing the frontend, make sure the Vite dev server (`npm run dev`) is running, or that you ran `npm run build` so `Beacon.UI/wwwroot` is up to date.
3. Check the browser console for errors and clear cache.

### Authentication failing

1. Confirm `AddBeaconCookieAuthentication("/")` is registered and `UseAuthentication` runs in the correct order.
2. Verify the authentication provider (e.g. `DatabaseAuthenticationProvider`) is registered.
3. For API-key callers, confirm the key's scope and project restriction allow the request.

### AI features not working

1. Verify `options.UseAI = true`.
2. Check the LLM configuration (provider, key) in `appsettings.json` or Admin Settings.
3. Confirm the provider key is valid and within quota.
4. Ensure the extended timeouts (B6) are configured.

---

## Production Considerations

### Security

- ✅ **Set a strong admin password** during first-run setup — there are no default credentials.
- ✅ **Keep the encryption key out of source control** — use environment variables or a secrets manager.
- ✅ **Enable HTTPS** in production.
- ✅ **Use OIDC/SSO** for production identity where possible.
- ✅ **Scope API keys** tightly (`Read`/`Execute`/`Admin` + project restrictions).

### Performance

- ✅ Configure database connection pooling.
- ✅ Adjust your job runner's worker count to match subscription load.
- ✅ Set query timeouts appropriate to your workloads.

### Monitoring

- ✅ Enable detailed logging for troubleshooting (no PII in logs).
- ✅ Use your job runner's dashboard to monitor job execution.
- ✅ Track LLM token usage and cost if AI is enabled.

---

## Next Steps

Now that Beacon is running:

1. **[Connect your first data source](../features/data-sources)** — add databases and APIs to monitor
2. **[Create your first query](../features/queries)** — define SQL monitoring queries (including cross-database)
3. **[Set up a subscription](../features/subscriptions)** — schedule automated execution
4. **[Configure notifications](../features/notifications)** — deliver results via Email, Teams, or Jira

## Additional Resources

- 📚 [Configuration Guide](configuration) — detailed configuration options
- 🚀 [Quick Start](quick-start) — create your first alert end to end
- 🎓 [Features Overview](../features/) — complete feature documentation
- 🐛 [Report Issues](https://github.com/MiBu/semantico/issues)
