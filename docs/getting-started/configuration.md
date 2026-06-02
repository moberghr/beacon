---
layout: default
title: Configuration
parent: Getting Started
nav_order: 3
---

# Configuration Guide

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
        options.AddBeaconScheduler<BeaconScheduler>();   // IBeaconScheduler (Hangfire-backed)
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
app.UseHangfireDashboard("/hangfire");
app.MapBeaconUi();                // React SPA at root /
```

{: .note }
> The React SPA is served at the **root URL `/`** by the `Beacon.UI` Razor Class Library (it builds from `Beacon.UI/web` into `Beacon.UI/wwwroot`). The REST API lives under `/beacon/api/*`, the MCP server at `/beacon/mcp`, the SignalR hub at `/beacon/api/hub`, and the OpenAPI document at `/openapi/v1.json`.

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

{: .note }
> The SPA is served at the root `/`, so notification links resolve to paths like `{BaseUrl}/notifications/{id}` — no `/beacon` UI prefix.

## Encryption Key Configuration ⚠️ REQUIRED

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

⚠️ **Never commit the encryption key to source control.**

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
    "Oidc": {
      "Authority": "https://login.example.com",
      "ClientId": "beacon",
      "ClientSecret": "${OIDC_CLIENT_SECRET}"
    }
  }
}
```

### API Keys

Beacon issues API keys for programmatic and MCP access:

- SHA256-hashed at rest; the raw key is shown **once** at creation
- Carry scopes: `Read`, `Execute`, `Admin`
- Support optional project restrictions
- Authenticated by `ApiKeyAuthMiddleware`, which runs **before** `UseAuthentication`

MCP clients may also use **JWT bearer** tokens.

{: .note }
> For complete user-management and provider documentation (external JWT setup, custom providers, roles), see the [User Management Guide](../features/user-management).

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

{: .note }
> For complete authorization documentation, see the [Authorization Guide](../features/authorization).

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

{: .warning }
> **Experimental Feature:** AI-powered features (auto-documentation, natural-language → query, anomaly detection) are experimental and may produce incorrect results. Configure only if you'll validate AI-generated content.

The LLM provider is **runtime-swappable** — it can be changed at runtime via Admin Settings without a restart. All LLM calls go through a request queue that enforces concurrency limits. Do not hard-code provider assumptions; read capabilities through the abstraction.

{: .note }
> LLM settings in `appsettings.json` act as startup defaults. Values configured in [Admin Settings](../features/admin-settings) take precedence and support hot-swapping providers.

### Supported Providers

- **OpenAI**
- **Anthropic Claude**
- **Azure OpenAI**

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
      "BaseUrl": "https://api.openai.com/v1",
      "Limits": {
        "MaxConcurrentRequests": 5,
        "RequestsPerMinute": 60,
        "MaxTokensPerRequest": 4000
      }
    }
  }
}
```

### Anthropic Claude

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "Anthropic",
      "ApiKey": "sk-ant-...",
      "Model": "claude-3-5-sonnet-20241022",
      "BaseUrl": "https://api.anthropic.com"
    }
  }
}
```

### Azure OpenAI

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "AzureOpenAI",
      "ApiKey": "your-azure-key",
      "Model": "your-deployment-name",
      "BaseUrl": "https://your-resource.openai.azure.com"
    }
  }
}
```

### Rate Limiting

The request queue caps concurrency and throughput:

| Setting | Description | Default | Recommended |
|---------|-------------|---------|-------------|
| `MaxConcurrentRequests` | Max parallel AI requests | 5 | 3–10 based on tier |
| `RequestsPerMinute` | Rate limit per minute | 60 | Match provider tier |
| `MaxTokensPerRequest` | Max tokens per request | 4000 | 4000–8000 |

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

Beacon's metadata database runs on **EF Core 9** with dual-provider support (PostgreSQL and SQL Server). The default schema is `beacon` (configurable). Dapper is used for hot paths.

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

Individual data-source connection strings are entered in the UI and encrypted at rest with your `Beacon:EncryptionKey`. See the [Data Sources Guide](../features/data-sources).

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

Beacon schedules recurring work through the `IBeaconScheduler` abstraction. The canonical implementation, `BeaconScheduler`, is backed by **Hangfire on PostgreSQL** (1-second poll, automatic retries disabled). Quartz.NET remains a valid alternative implementation of the same interface.

### Hangfire Storage (canonical)

```csharp
using Hangfire;
using Hangfire.PostgreSql;

builder.Services.AddHangfire((provider, config) => config
    .SetDataCompatibilityLevel(CompatibilityLevel.Version_170)
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseFilter(new AutomaticRetryAttribute { Attempts = 0 }) // retries disabled
    .UsePostgreSqlStorage(connectionString, new PostgreSqlStorageOptions
    {
        PrepareSchemaIfNecessary = true,
        QueuePollInterval = TimeSpan.FromSeconds(1)
    }));

builder.Services.AddHangfireServer(options =>
{
    options.WorkerCount = 10; // default: CPU core count
});
```

**Worker-count guidelines:**

- Light load (< 100 subscriptions): 2–4 workers
- Medium load (100–500 subscriptions): 5–10 workers
- Heavy load (500+ subscriptions): 10–20 workers

### Scheduler Implementation

```csharp
using Hangfire;
using Beacon.Core.Worker;

public class BeaconScheduler : IBeaconScheduler
{
    private readonly IRecurringJobManager _recurringJobManager;

    public BeaconScheduler(IRecurringJobManager recurringJobManager)
    {
        _recurringJobManager = recurringJobManager;
    }

    public void AddOrUpdate(int subscriptionId, string subscriptionName, string cron)
    {
        var jobKey = $"{subscriptionId} - {subscriptionName}";
        _recurringJobManager.AddOrUpdate<IJobService>(
            jobKey,
            x => x.ExecuteQuery(subscriptionId),
            cron);
    }

    public void Remove(int subscriptionId, string subscriptionName)
    {
        var jobKey = $"{subscriptionId} - {subscriptionName}";
        _recurringJobManager.RemoveIfExists(jobKey);
    }
}

// Register it
options.AddBeaconScheduler<BeaconScheduler>();
```

The Hangfire dashboard mounts at `/hangfire` (admin only).

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

Teams and Jira notifications work out of the box — configure them per recipient in the UI. See the [Notifications Guide](../features/notifications).

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

- ✅ Use User Secrets for development
- ✅ Use Azure Key Vault or similar for production
- ✅ Use read-only users for monitored data sources
- ✅ Enable SSL/TLS in production
- ✅ Rotate passwords regularly

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

## Logging

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Beacon": "Debug",
      "Hangfire": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  }
}
```

{: .warning }
> **No PII in logs.** User-supplied query text, connection strings, full row payloads, and auth tokens are never logged — Beacon logs identifiers and counts only.

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
    "LLM": {
      "Provider": "OpenAI",
      "ApiKey": "${LLM_API_KEY}",
      "Model": "gpt-4o"
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
      "Beacon": "Information",
      "Hangfire": "Warning"
    }
  }
}
```

{: .note }
> LLM settings in `appsettings.json` are optional startup defaults. Once configured via [Admin Settings](../features/admin-settings), database values take precedence.

## Next Steps

<div class="code-example" markdown="1">
📊 **[Features →](../features/)**

Explore Data Sources, Queries, Subscriptions, and more
</div>

<div class="code-example" markdown="1">
🚀 **[Quick Start →](quick-start)**

Create your first alert end to end
</div>

<div class="code-example" markdown="1">
🤖 **[MCP Server →](../features/mcp-server)**

Integrate Beacon with AI agents over MCP
</div>
