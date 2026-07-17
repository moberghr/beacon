---
title: Configuration
description: Configure Beacon connection strings, the encryption key, authentication, AI/LLM, email, scheduling, and the metadata database.
---

Complete reference for configuring Beacon — connection strings, the required encryption key, authentication, AI/LLM, email, scheduling, and the metadata database.

This guide applies to both delivery modes: running the `Beacon.SampleProject` host and embedding the `Beacon.*` NuGet packages in your own ASP.NET Core app. Configuration is identical either way.

## Configuration in Program.cs

Beacon is wired in `Program.cs`. The canonical setup (from `Beacon.SampleProject`) is:

```csharp
using Beacon.AI;
using Beacon.Api;
using Beacon.Core;
using Beacon.Core.PostgreSql;
using Beacon.MCP;
using Beacon.UI;

// Host identity + SignalR plumbing
builder.Services.AddBeaconHostInfrastructure();

// Core services, scheduler, connectors, metadata provider
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();   // your IBeaconScheduler implementation
        options.BaseUrl = "https://localhost:7187";
        options.UseAI = true;
        options.AddEmailAdapter<BeaconMailSender>();
        options.Authorization.Enabled = true;
        options.Authentication.EnableLoginForm = true;
        options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();
        options.UserManagement = new UserManagementOptions { Enabled = true };
    })
    .AddPostgreSqlConnector()
    .AddSqlServerConnector()
    .AddMySqlConnector()
    // ... other connectors ...
    .UsePostgreSql(builder.Configuration.GetConnectionString("BeaconContext")!, "beacon");

builder.Services.AddBeaconCookieAuthentication("/");
builder.Services.AddBeaconOidcAuthentication(builder.Configuration); // optional SSO
builder.Services.AddBeaconAI(builder.Configuration);
builder.Services.AddBeaconMcp();
builder.Services.AddOpenApi();

var app = builder.Build();

app.UseStaticFiles();
app.UseMiddleware<ApiKeyAuthMiddleware>();
app.UseAuthentication();
app.UseMiddleware<BeaconCookieAuthMiddleware>();
app.UseAuthorization();
app.UseAntiforgery();

app.MapOpenApi();                 // /openapi/v1.json
app.MapBeaconApi();               // /beacon/api/*
app.MapLoginEndpoints("/beacon", beaconConfiguration);
app.MapHub<BeaconHub>("/beacon/api/hub").RequireAuthorization();
app.MapMcp("/beacon/mcp").RequireAuthorization();
app.MapBeaconUi();                // React SPA at root /
```

:::note
The React SPA is served at the **root URL `/`** by the `Beacon.UI` Razor Class Library (it builds from `src/Beacon.UI/web` into `src/Beacon.UI/wwwroot`). The REST API lives under `/beacon/api/*`, the MCP server at `/beacon/mcp`, the SignalR hub at `/beacon/api/hub`, and the OpenAPI document at `/openapi/v1.json`.
:::

## Base URL Configuration

`BaseUrl` is the public origin where Beacon is hosted. It's used to build clickable links in notifications (e.g. Teams cards) back to the details page in the app.

```csharp
options.BaseUrl = "https://localhost:7187";        // development
options.BaseUrl = "https://staging.example.com";   // staging
options.BaseUrl = "https://beacon.example.com";     // production
options.BaseUrl = builder.Configuration["Beacon:BaseUrl"]; // from config
```

### appsettings.json

```json
{
  "Beacon": {
    "BaseUrl": "https://beacon.example.com",
    "EncryptionKey": "your-secure-32-byte-base64-key"
  }
}
```

:::note
The SPA is served at the root `/`, so notification links resolve to paths like `{BaseUrl}/notifications/{id}` — no `/beacon` UI prefix.
:::

## Encryption Key Configuration (Required)

`Beacon:EncryptionKey` is **required**. It encrypts sensitive data — most importantly data-source connection strings — at rest using AES-256.

### Generate a Secure Key

```bash
openssl rand -base64 32
```

This produces a 32-byte key (base64-encoded) suitable for AES-256.

### Configuration

```json
{
  "Beacon": {
    "EncryptionKey": "k8J3m9Lp2Nq5Rt8Vw1Yz4Bc7Df0Gh3Jk6Lm9No2Pq="
  }
}
```

### Security Best Practices

:::caution
Never commit the encryption key to source control.
:::

**Development — User Secrets:**
```bash
dotnet user-secrets set "Beacon:EncryptionKey" "$(openssl rand -base64 32)"
```

**Production — environment variable:**
```bash
export BEACON_ENCRYPTION_KEY="your-production-key-here"
```
```json
{
  "Beacon": {
    "EncryptionKey": "${BEACON_ENCRYPTION_KEY}"
  }
}
```

**Production — Azure Key Vault:**
```csharp
var keyVaultEndpoint = new Uri(Environment.GetEnvironmentVariable("VaultUri"));
var secretClient = new SecretClient(keyVaultEndpoint, new DefaultAzureCredential());
var secret = await secretClient.GetSecretAsync("BeaconEncryptionKey");
builder.Configuration["Beacon:EncryptionKey"] = secret.Value.Value;
```

### What Gets Encrypted

- Data-source connection strings (stored in the metadata database)
- Other sensitive configuration values as needed

API keys are **not** encrypted — they are SHA256-hashed, and the raw key is shown to the user once at creation.

### Error if Missing

If the encryption key is not configured, Beacon throws on startup:

```
InvalidOperationException: Beacon:EncryptionKey must be configured.
Generate a secure key with: openssl rand -base64 32
```

## Authentication

Beacon authentication is **cookie-based**. The `Beacon.Auth` cookie is `HttpOnly` with `SameSite=Lax`. Identity is resolved through a pluggable `IBeaconAuthenticationProvider`. There is **no basic auth and no default `admin`/`admin` account** — the first-run setup flow creates the initial admin user.

### Login Form (cookie auth)

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
        options.Authentication.EnableLoginForm = true;
        options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();
    })
    .UsePostgreSql(connectionString, "beacon");

// Login redirect target — the SPA serves the login form at /login
builder.Services.AddBeaconCookieAuthentication("/");

app.MapLoginEndpoints("/beacon", beaconConfiguration);
```

### Authentication Providers

| Provider | Use Case |
|----------|----------|
| `DatabaseAuthenticationProvider` | Internal users with passwords stored in Beacon |
| `JwtExternalApiAuthenticationProvider` | External JWT / OAuth identity provider |
| `HybridAuthenticationProvider` | Both internal and external users |

### Optional: OIDC / SSO

```csharp
builder.Services.AddBeaconOidcAuthentication(builder.Configuration);
```

```json
{
  "Beacon": {
    "Authentication": {
      "Oidc": {
        "Enabled": true,
        "Authority": "https://login.microsoftonline.com/{YOUR_TENANT_ID}/v2.0",
        "ClientId": "{YOUR_CLIENT_ID}",
        "ClientSecret": "${OIDC_CLIENT_SECRET}",
        "CallbackPath": "/signin-oidc",
        "Scopes": ["openid", "profile", "email"],
        "DefaultRoleName": "Viewer",
        "DisplayName": "Microsoft",
        "McpJwksEndpoint": "https://login.microsoftonline.com/{YOUR_TENANT_ID}/discovery/v2.0/keys"
      }
    }
  }
}
```

`DefaultRoleName` is the role assigned to first-time SSO users; `DisplayName` labels the SSO button on the login page.

### API Keys

Beacon issues API keys for programmatic and MCP access — see the [API Keys Guide](/features/api-keys/):

- SHA256-hashed at rest; the raw key is shown **once** at creation
- Carry scopes: `Read`, `Execute`, `Admin`
- Support optional project restrictions
- Authenticated by `ApiKeyAuthMiddleware`, which runs **before** `UseAuthentication`

### JWT Bearer for MCP Clients

MCP clients can also authenticate with **JWT bearer** tokens issued by your OIDC provider. When OIDC is enabled, set `McpJwksEndpoint` to the provider's JWKS (signing keys) URL — Beacon validates the token's signature, issuer, and audience against it. This lets AI assistants use the same identity provider as your users, without cookies or long-lived API keys.

:::note
For complete user-management and provider documentation (external JWT setup, custom providers, roles), see the [User Management Guide](/features/user-management/).
:::

## Authorization

Authorization is controlled via options and a pluggable provider.

```csharp
options.Authorization.Enabled = true;
options.AddAuthorizationProvider<MyCustomProvider>();
```

### Custom Authorization Provider

```csharp
public class MyAuthorizationProvider : IBeaconAuthorizationProvider
{
    private readonly IBeaconUserContext _userContext;

    public MyAuthorizationProvider(IBeaconUserContext userContext)
    {
        _userContext = userContext;
    }

    public Task<bool> HasReadPermissionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_userContext.IsAuthenticated);

    public Task<bool> HasWritePermissionAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_userContext.HasClaim(BeaconClaims.Role, "Admin"));

    // Implement other methods...
}

// Register it
options.AddAuthorizationProvider<MyAuthorizationProvider>();
```

:::note
For complete authorization documentation, see the [Authorization Guide](/features/authorization/).
:::

## User Management

```csharp
options.Authorization.Enabled = true;
options.Authentication.EnableLoginForm = true;
options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();

options.UserManagement = new UserManagementOptions
{
    Enabled = true,
    AllowInternalUsers = true,
    MinimumPasswordLength = 8,
    RequirePasswordComplexity = true
};
```

| Option | Description | Default |
|--------|-------------|---------|
| `Enabled` | Enable user-management features | `false` |
| `AllowInternalUsers` | Allow password-based users | `true` |
| `MinimumPasswordLength` | Minimum password length | `8` |
| `RequirePasswordComplexity` | Require mixed case, numbers, symbols | `true` |

## AI / LLM Configuration (Optional — Experimental)

:::caution[Experimental]
AI-powered features (auto-documentation, natural-language → query, anomaly detection) are experimental and may produce incorrect results. Configure only if you will validate AI-generated content.
:::

The LLM provider is **runtime-swappable** — it can be changed at runtime via Admin Settings without a restart. All LLM calls go through a request queue that enforces concurrency limits. Do not hard-code provider assumptions; read capabilities through the abstraction.

:::note
LLM settings in `appsettings.json` act as startup defaults. Values configured in [Admin Settings](/features/admin-settings/) take precedence and support hot-swapping providers.
:::

### Supported Providers

The `Provider` value is one of: `OpenAI`, `Claude` (Anthropic), `AzureOpenAI`, `Bedrock` (AWS).

### Basic Configuration

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "OpenAI",
      "ApiKey": "sk-your-api-key-here",
      "Model": "gpt-4o"
    }
  }
}
```

### Complete Configuration

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "OpenAI",
      "ApiKey": "sk-your-api-key-here",
      "Model": "gpt-4o",
      "FastModel": "gpt-4o-mini",
      "Limits": {
        "MaxConcurrentRequests": 50,
        "TokensPerMinute": 80000,
        "RequestsPerMinute": 1000,
        "MonthlyBudget": 100.00
      }
    }
  }
}
```

`FastModel` is optional: Beacon routes simple, high-volume operations (classification, short summaries) to the fast model and reserves the main `Model` for complex generation. If omitted, sensible per-provider defaults are used (`gpt-4o-mini` for OpenAI, `claude-haiku-4-20250514` for Claude).

### Anthropic Claude

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "Claude",
      "ApiKey": "sk-ant-...",
      "Model": "claude-sonnet-4-20250514"
    }
  }
}
```

### Azure OpenAI

Azure requires the `Endpoint` of your resource; `Model` is your deployment name.

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "AzureOpenAI",
      "ApiKey": "your-azure-key",
      "Model": "your-deployment-name",
      "Endpoint": "https://your-resource.openai.azure.com"
    }
  }
}
```

### AWS Bedrock

Bedrock requires a `Region`. Credentials come either from the default AWS chain (IAM role, environment variables) — leave `ApiKey` empty — or explicitly as `"accessKey:secretKey"` (plus an optional `SessionToken` for temporary credentials).

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "Bedrock",
      "ApiKey": "",
      "Region": "eu-west-1",
      "Model": "eu.anthropic.claude-sonnet-4-5-20250929-v1:0"
    }
  }
}
```

### Rate Limiting

The request queue caps concurrency and throughput:

| Setting | Description |
|---------|-------------|
| `MaxConcurrentRequests` | Max parallel AI requests |
| `TokensPerMinute` | Token throughput cap |
| `RequestsPerMinute` | Request rate cap — match your provider tier |
| `MonthlyBudget` | Monthly spend cap (USD) tracked by the usage service |

### Production — Use Environment Variables

```json
{
  "Beacon": {
    "LLM": {
      "ApiKey": "${LLM_API_KEY}"
    }
  }
}
```

## Metadata Database Provider

Beacon's metadata database runs on **EF Core 10** with dual-provider support (PostgreSQL and SQL Server). The default schema is `beacon` (configurable). Dapper is used for hot paths.

### PostgreSQL

```csharp
using Beacon.Core;
using Beacon.Core.PostgreSql;

builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
    })
    .UsePostgreSql(
        builder.Configuration.GetConnectionString("BeaconContext")!,
        "beacon"); // optional schema, defaults to "beacon"
```

```json
{
  "ConnectionStrings": {
    "BeaconContext": "Host=localhost;Database=beacon;Username=postgres;Password=yourpassword"
  }
}
```

### SQL Server

```csharp
using Beacon.Core;
using Beacon.Core.SqlServer;

builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
    })
    .UseSqlServer(
        builder.Configuration.GetConnectionString("BeaconContext")!,
        "beacon");
```

```json
{
  "ConnectionStrings": {
    "BeaconContext": "Server=localhost;Database=beacon;User Id=sa;Password=YourPassword123!;TrustServerCertificate=True"
  }
}
```

## Data-Source Connectors

Register a connector for each kind of data source you intend to monitor. Beacon ships nine connectors:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options => { /* ... */ })
    .AddPostgreSqlConnector()
    .AddSqlServerConnector()
    .AddMySqlConnector()
    .AddBigQueryConnector()
    .AddSnowflakeConnector()
    .AddDatabricksConnector()
    .AddAzureSynapseConnector()
    .AddCloudWatchConnector()
    .AddApiConnector()
    .UsePostgreSql(connectionString, "beacon");
```

| Connector | Package |
|---|---|
| PostgreSQL | `Beacon.Connector.PostgreSql` |
| SQL Server | `Beacon.Connector.SqlServer` |
| MySQL | `Beacon.Connector.MySql` |
| Google BigQuery | `Beacon.Connector.BigQuery` |
| Snowflake | `Beacon.Connector.Snowflake` |
| Databricks | `Beacon.Connector.Databricks` |
| Azure Synapse | `Beacon.Connector.AzureSynapse` |
| AWS CloudWatch | `Beacon.Connector.CloudWatch` |
| Generic REST API | `Beacon.Connector.Api` |

Individual data-source connection strings are entered in the UI and encrypted at rest with your `Beacon:EncryptionKey`. See the [Data Sources Guide](/features/data-sources/).

## Connection String Reference

### PostgreSQL

```
Host=hostname;Port=5432;Database=dbname;Username=user;Password=pass
Host=hostname;Database=dbname;Username=user;Password=pass;SSL Mode=Require
Host=hostname;Database=dbname;Username=user;Password=pass;Pooling=true;MinPoolSize=0;MaxPoolSize=100
```

### SQL Server

```
Server=hostname;Database=dbname;User Id=user;Password=pass;TrustServerCertificate=True
Server=hostname;Database=dbname;Integrated Security=True
Server=hostname;Database=dbname;User Id=user;Password=pass;Encrypt=True;TrustServerCertificate=False
```

### MySQL

```
Server=hostname;Port=3306;Database=dbname;Uid=user;Pwd=pass
Server=hostname;Database=dbname;Uid=user;Pwd=pass;SslMode=Required
```

## Scheduling

Beacon does not bundle a job runner. All recurring work goes through the `IBeaconScheduler` abstraction — you plug in the scheduler your host already uses:

```csharp
namespace Beacon.Core.Worker;

public interface IBeaconScheduler
{
    void AddOrUpdate(int subscriptionId, string subscriptionName, string cron);
    void Remove(int subscriptionId, string subscriptionName);

    // optional — data-quality contract evaluation schedules
    void AddOrUpdateDataQualityJob(int contractId, string contractName, string cron);
    void RemoveDataQualityJob(int contractId, string contractName);

    // optional — fire-and-forget background work used by the AI features;
    // returns the job id (used to correlate JobStatusChanged push events)
    string EnqueueProjectDocumentation(int projectId, int userId, string? notifyUserId);
    string EnqueueAiActorThinkCycle(int actorId, int subscriptionId);
}
```

The optional members have default implementations that throw, so hosts that don't use data-quality contracts or AI features only implement the two subscription methods.

Beacon calls `AddOrUpdate` whenever a subscription is created or its cron changes, and `Remove` when it's deleted or disabled. Your implementation maps those calls onto recurring jobs that invoke `IJobService.ExecuteQuery(subscriptionId)` on the cron schedule.

### Scheduler Implementation

```csharp
using Beacon.Core.Worker;

public class BeaconScheduler : IBeaconScheduler
{
    public void AddOrUpdate(int subscriptionId, string subscriptionName, string cron)
    {
        // register/update a recurring job in your job runner that calls
        // IJobService.ExecuteQuery(subscriptionId) on the given cron schedule
    }

    public void Remove(int subscriptionId, string subscriptionName)
    {
        // remove the recurring job
    }
}

// Register it
options.AddBeaconScheduler<BeaconScheduler>();
```

Any job runner with recurring/cron support works. We recommend [Moberg Warp](https://moberghr.github.io/warp/) — define an `IJob` that calls `IJobService.ExecuteQuery` and map `AddOrUpdate`/`Remove` onto Warp's `AddOrUpdateRecurringJob`/remove APIs; you get retries, concurrency guards, and a job dashboard out of the box. Quartz.NET is another valid choice. `Beacon.SampleProject` ships a complete working reference implementation you can copy as a starting point.

## Email Adapter

Beacon does not ship a default email implementation. Provide your own `IEmailAdapter` and register it via `options.AddEmailAdapter<...>()`.

```csharp
using Beacon.Core.Adapters.Mail;
using System.Net;
using System.Net.Mail;

public class SmtpEmailAdapter : IEmailAdapter
{
    private readonly IConfiguration _configuration;

    public SmtpEmailAdapter(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task SendEmailAsync(string to, string subject, string body, Stream? attachment = null)
    {
        using var smtpClient = new SmtpClient
        {
            Host = _configuration["Email:SmtpHost"],
            Port = int.Parse(_configuration["Email:SmtpPort"]),
            EnableSsl = true,
            Credentials = new NetworkCredential(
                _configuration["Email:Username"],
                _configuration["Email:Password"])
        };

        var message = new MailMessage
        {
            From = new MailAddress(
                _configuration["Email:FromAddress"],
                _configuration["Email:FromName"]),
            Subject = subject,
            Body = body,
            IsBodyHtml = true
        };

        message.To.Add(to);

        if (attachment != null)
        {
            message.Attachments.Add(new Attachment(attachment, "results.csv", "text/csv"));
        }

        await smtpClient.SendMailAsync(message);
    }
}
```

Register it:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
        options.AddEmailAdapter<SmtpEmailAdapter>();
    })
    .UsePostgreSql(connectionString, "beacon");
```

```json
{
  "Email": {
    "SmtpHost": "smtp.gmail.com",
    "SmtpPort": "587",
    "Username": "alerts@yourdomain.com",
    "Password": "your-app-password",
    "FromAddress": "alerts@yourdomain.com",
    "FromName": "Beacon Alerts"
  }
}
```

Teams and Jira notifications work out of the box — configure them per recipient in the UI. See the [Notifications Guide](/features/notifications/).

## Schema Configuration

The default schema is `beacon`. Override it for multi-tenancy or environment separation:

```csharp
var schema = builder.Configuration["Beacon:Schema"] ?? "beacon";

builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
    })
    .UsePostgreSql(
        builder.Configuration.GetConnectionString("BeaconContext")!,
        schema);
```

```json
{
  "Beacon": {
    "Schema": "production_beacon"
  }
}
```

## Metadata Loading (Large Databases)

Beacon introspects the schemas of your monitored data sources to power the database explorer, documentation, and MCP context. For very large databases you can bound that work:

```json
{
  "Beacon": {
    "MetadataLoading": {
      "Enabled": true,
      "MaxTables": 500,
      "MaxColumnsPerTable": 200,
      "LoadTableNamesOnly": false,
      "IncludeSchemas": ["public", "app"],
      "ExcludeSchemas": ["information_schema", "pg_catalog"]
    }
  }
}
```

| Setting | Description |
|---------|-------------|
| `Enabled` | Set `false` to disable metadata loading entirely |
| `MaxTables` | Cap on tables loaded per data source (`0` = unlimited) |
| `MaxColumnsPerTable` | Cap on columns loaded per table (`0` = unlimited) |
| `LoadTableNamesOnly` | Load only table names, skipping columns |
| `IncludeSchemas` | Whitelist — only load these schemas |
| `ExcludeSchemas` | Blacklist — skip these schemas |

## Security

### Database User Permissions

**Beacon metadata database (PostgreSQL):**
```sql
CREATE USER beacon WITH PASSWORD 'strong-password';
GRANT ALL PRIVILEGES ON DATABASE beacon TO beacon;
GRANT ALL ON SCHEMA beacon TO beacon;
```

**Monitored data sources (read-only recommended):**
```sql
CREATE USER beacon_readonly WITH PASSWORD 'strong-password';
GRANT CONNECT ON DATABASE your_database TO beacon_readonly;
GRANT USAGE ON SCHEMA public TO beacon_readonly;
GRANT SELECT ON ALL TABLES IN SCHEMA public TO beacon_readonly;
ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT ON TABLES TO beacon_readonly;
```

### Connection String Security

- Use User Secrets for development
- Use Azure Key Vault or similar for production
- Use read-only users for monitored data sources
- Enable SSL/TLS in production
- Rotate passwords regularly

**User Secrets (development):**
```bash
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:BeaconContext" "Host=localhost;Database=beacon;Username=postgres;Password=devpassword"
```

**Azure Key Vault (production):**
```csharp
builder.Configuration.AddAzureKeyVault(
    new Uri($"https://{keyVaultName}.vault.azure.net/"),
    new DefaultAzureCredential());
```

### Reverse Proxy / Forwarded Headers

When Beacon runs behind a reverse proxy (nginx, Traefik, a cloud load balancer), enable forwarded-headers processing so rate limiting and redirects see the real client IP and scheme. It is **off by default** and trusts only the proxies you whitelist:

```json
{
  "Beacon": {
    "ForwardedHeaders": {
      "Enabled": true,
      "KnownProxies": ["10.0.0.5"]
    }
  }
}
```

:::caution
Only enable this when a trusted proxy actually sits in front of Beacon, and always whitelist its address. Blindly trusting `X-Forwarded-For` lets clients spoof their IP past the login rate limiter.
:::

## Logging

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Beacon": "Debug",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

:::caution[No PII in logs]
User-supplied query text, connection strings, full row payloads, and auth tokens are never logged — Beacon logs identifiers and counts only.
:::

## Complete appsettings.json Example

```json
{
  "ConnectionStrings": {
    "BeaconContext": "Host=localhost;Database=beacon;Username=beacon;Password=secretpass;Pooling=true;MaxPoolSize=50"
  },
  "Beacon": {
    "EncryptionKey": "your-secure-32-byte-base64-key",
    "Schema": "beacon",
    "BaseUrl": "https://beacon.example.com",
    "ForwardedHeaders": {
      "Enabled": false,
      "KnownProxies": []
    },
    "LLM": {
      "Provider": "OpenAI",
      "ApiKey": "${LLM_API_KEY}",
      "Model": "gpt-4o",
      "FastModel": "gpt-4o-mini"
    }
  },
  "Email": {
    "SmtpHost": "smtp.gmail.com",
    "SmtpPort": "587",
    "Username": "alerts@yourdomain.com",
    "Password": "app-specific-password",
    "FromAddress": "alerts@yourdomain.com",
    "FromName": "Beacon Alerts"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Beacon": "Information"
    }
  }
}
```

:::note
LLM settings in `appsettings.json` are optional startup defaults. Once configured via [Admin Settings](/features/admin-settings/), database values take precedence.
:::

## Next Steps

- [Features](/features/) — explore Data Sources, Queries, Subscriptions, and more
- [Quick Start](/getting-started/quick-start/) — create your first alert end to end
- [MCP Server](/features/mcp-server/) — integrate Beacon with AI agents over MCP
