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

// Host identity (claims transformation)
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
app.MapBeaconApi();               // /beacon/api/* + the SignalR hub
app.MapLoginEndpoints("/beacon", beaconConfiguration);
app.MapMcp("/beacon/mcp").RequireAuthorization();
app.MapBeaconUi();                // React SPA at root /
```

:::note
The React SPA is served at the **root URL `/`** by the `Beacon.UI` Razor Class Library (it builds from `src/Beacon.UI/web` into `src/Beacon.UI/wwwroot`). The REST API lives under `/beacon/api/*`, the MCP server at `/beacon/mcp`, the SignalR hub at `/beacon/api/hub`, and the OpenAPI document at `/openapi/v1.json`. The hub is wired automatically by `AddBeaconApiServices()` / `MapBeaconApi()` — see [Real-time](/getting-started/installation/#real-time-signalr) for the opt-out.
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

### Public Base URL (behind a reverse proxy)

`Beacon:PublicBaseUrl` is the public origin advertised in the MCP discovery documents (`/.well-known/*`) and in the `WWW-Authenticate: Bearer resource_metadata="…"` challenge that unauthenticated MCP requests receive.

```json
{
  "Beacon": {
    "PublicBaseUrl": "https://beacon.example.com"
  }
}
```

Leave it empty and those URLs are derived from the incoming request's `Host` header — which a client or a misconfigured proxy controls. The host logs a startup warning when it is unset. Set it in any deployment that sits behind a proxy or load balancer. See the [MCP Server guide](/features/mcp-server/#discovery-endpoints).

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
- Notification recipient destinations and webhook headers (Slack/Teams webhook URLs, Jira API tokens, webhook auth headers). Rows saved by an older version are encrypted by the `IRecipientSecretEncryptionService` job (see [Notification Destinations](#notification-destinations-optional)) or when they are next saved.
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
        "AllowedTenants": ["{YOUR_TENANT_ID}"],
        "BlockGuests": true,
        "RequiredRoles": [],
        "RequiredGroups": [],
        "DisplayName": "Microsoft",
        "McpJwksEndpoint": "https://login.microsoftonline.com/{YOUR_TENANT_ID}/discovery/v2.0/keys"
      }
    }
  }
}
```

Beacon decides who may sign in **before** it provisions anything. A subject that is not admitted gets a "not permitted" message on the login page and no Beacon user is created for it.

| Option | Description | Default |
|--------|-------------|---------|
| `AllowedTenants` | Entra tenant ids (`tid` claim) whose users may sign in. **Startup fails** when SSO is enabled with no tenant listed and `AllowAnyTenant` off. | empty |
| `AllowAnyTenant` | Skip the tenant check. Only for an identity provider that issues no `tid` claim (the `Authority` then decides who can sign in). | `false` |
| `BlockGuests` | Refuse Entra B2B guests: an `acct` claim of `1`, or an `idp` claim that is not exactly the token issuer or one of the tenant's own Entra issuers. Applies to tokens that carry `tid`. A token with neither `acct` nor `idp` is admitted as a member (a one-time Warning says so). | `true` |
| `RequiredRoles` / `RequiredGroups` | App roles (`roles` claim) / group object ids (`groups` claim). When either is set, a user must hold at least one listed role or group (case-insensitive). An app role listed here must exist in the app registration and be assigned, or nobody is admitted. | empty |
| `DefaultRoleName` | Role given to a user provisioned on first SSO sign-in. **Unset means no role**: the user can sign in but sees nothing until an administrator assigns one. | unset |
| `EnableFrontChannelLogout` | Serve the OIDC front-channel sign-out path (`/signout-oidc`). Off by default: it is an anonymous `GET` that ends the session. | `false` |

For reliable guest detection, add `acct` as an optional claim (ID and access tokens) in the Entra app registration: `idp` alone only reveals guests whose home is another identity provider, and while `BlockGuests` is on Beacon logs a one-time Warning when a token carries neither claim. With `AllowAnyTenant` on and an Entra authority, Beacon logs a Warning at startup, since every tenant the authority accepts can then sign in.

Prefer **app roles** in `RequiredRoles` over `RequiredGroups`: Entra leaves the `groups` claim out of a token when the user belongs to more groups than fit (the "overage" case, about 200 for a JWT), and such a user then fails the group requirement. As defence in depth, set the enterprise application to **assignment required** and assign only the approved users or groups. `DisplayName` labels the SSO button on the login page.

### API Keys

Beacon issues API keys for programmatic and MCP access — see the [API Keys Guide](/features/api-keys/):

- SHA256-hashed at rest; the raw key is shown **once** at creation
- Carry scopes: `Read`, `Execute`, `Admin`
- Support optional project restrictions
- Authenticated by `ApiKeyAuthMiddleware`, which runs **before** `UseAuthentication`

### JWT Bearer for MCP Clients

MCP clients can also authenticate with **JWT bearer** tokens issued by your OIDC provider. When OIDC is enabled, set `McpJwksEndpoint` to the provider's JWKS (signing keys) URL — the sample host then enables bearer tokens with the OIDC authority as the issuer and the client id as the audience. This lets AI assistants use the same identity provider as your users, without cookies or long-lived API keys.

In your own host, bearer tokens are enabled with `AddBeaconJwtAuthentication`. Whenever bearer authentication or an external login endpoint is on, **at least one issuer and one audience are required**, issuer, audience and lifetime validation cannot be turned off, and `ClockSkew` may be at most five minutes — the host refuses to start otherwise:

```csharp
builder.Services.AddBeaconJwtAuthentication(jwt =>
{
    jwt.EnableBearerAuthentication = true;
    jwt.Validation.JwksEndpoint = "https://login.microsoftonline.com/{tenant}/discovery/v2.0/keys";
    jwt.Validation.ValidIssuer = "https://login.microsoftonline.com/{tenant}/v2.0";
    jwt.Validation.ValidAudience = "{beacon-client-id}"; // the application (client) id GUID for v2.0 access tokens
});
```

Match the issuer and audience to the access-token version your app registration issues (`accessTokenAcceptedVersion` in its manifest). A **v2.0** access token (`accessTokenAcceptedVersion: 2`) has the issuer `https://login.microsoftonline.com/{tenant}/v2.0` and the application's client id GUID as `aud`. A **v1.0** token (the default, `null` or `1`) has the issuer `https://sts.windows.net/{tenant}/` and the Application ID URI (for example `api://{beacon-client-id}`) as `aud`. List both in `ValidIssuers` / `ValidAudiences` only if you accept both versions.

On `/beacon/mcp` the token is mapped by the MCP caller configuration (see [Entra MCP callers](/features/mcp-entra-callers/)). On every other route a bearer token must be an access token (an ID token is refused), must pass the SSO admission rules when the SSO authority issued it, and must name an **existing, enabled, external Beacon user** — by its subject and issuer, or a pre-registered user's subject when only one issuer is configured (see [User Management → Option 2](/features/user-management/#option-2-external-identity-provider-jwtoauth)). The session gets that user's **Beacon roles**, never roles from the token. Any other token gets `401` with `WWW-Authenticate: Bearer error="invalid_token"`. This needs user management; without it, bearer tokens are refused outside `/beacon/mcp`.

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

When `Authorization.Enabled` or user management is on, Beacon enforces Viewer vs Editor server-side on `/beacon/api`: `GET` requests need read permission (Viewer or above) and every other method needs write permission (Editor or above). The exceptions are running query previews and changing your own password, which a Viewer may do. Read-scoped API keys cannot call mutating endpoints, whatever role the key's user holds.

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
| `SetupToken` | Secret the first-run setup page must present; at least 32 characters, or startup fails. Also read from `Beacon:UserManagement:SetupToken`. When unset, Beacon generates one per process and prints it once to the process console (standard error, never the log) while no super admin exists. Required when several replicas serve the setup page. | unset (generated) |

:::caution[Upgrading from 4.5]
Identity and admission are tighter: first-run setup needs a setup token, SSO needs `AllowedTenants` (or `AllowAnyTenant`) and no longer gives new users a role by default, JWT issuer, audience and lifetime validation are mandatory, REST bearer tokens must name an existing external Beacon user, and `GET /beacon/api/auth/signout` is gone. See [User Management → Upgrading from 4.5](/features/user-management/#upgrading-from-45) for what to change.
:::

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

## Notification Destinations (Optional)

:::caution[Upgrading]
This version changes how notifications are managed and delivered. Before upgrading a host:

- **Recipients outside the destination policy stop delivering.** Plain `http://` webhooks, private or internal webhook hosts, an on-premises Jira, retired Office 365 connector URLs (`outlook.office.com`) and anything else outside the rules below fail with "the destination is not allowed by policy". Allow them with `AllowedHosts:{Type}` and, for private addresses, `AllowedPrivateNetworks:{Type}`, or re-create them with a supported URL.
- **Only `Authorization`, `Api-Key`, `X-Api-Key` and `Ocp-Apim-Subscription-Key` webhook headers are sent.** Add any other header a webhook needs to `AllowedHeaders`; until then those recipients fail.
- **Email recipients must be bare addresses** (`ops@example.com, risk@example.com`); `Name <address>` forms fail until re-saved.
- **The system HTTP proxy is ignored** unless `UseSystemProxy` is `true`. Hosts that can only reach the internet through a proxy must turn it on (and then list `AllowedHosts:Webhook`). In proxy mode the proxy, not Beacon, has the last word on which address a call reaches; see below.
- **Rolling back gets harder.** This version stores every recipient it saves with encrypted secrets, and an earlier version cannot read them: after a rollback, recipients created or edited on this version stop delivering until their destinations and headers are entered again.
- **Encrypt the stored secrets, explicitly and once.** Recipients saved by an earlier version keep working but stay unencrypted until `IRecipientSecretEncryptionService.EncryptStoredSecretsAsync` runs or they are saved again. Nothing runs it automatically. Run it yourself only when every node runs this version (no older instance is still reading recipients) and you no longer intend to roll back, because it rewrites every stored secret, archived recipients included, and after it a rollback means re-entering all of them. With Warp, enqueue the sample's job once, for example from a maintenance endpoint or a one-off script:

  ```csharp
  var publisher = scope.ServiceProvider.GetRequiredService<IPublisher>();
  await publisher.Enqueue(new EncryptRecipientSecretsJob());   // handler calls EncryptStoredSecretsAsync
  await publisher.SaveChangesAsync();
  ```

  It is idempotent, never overwrites a recipient edited while it runs, logs its counts, and fails (logging nothing but recipient ids) if any recipient could not be encrypted, so it can simply be run again. Once it has succeeded, set `RequireEncryptedSecrets` to `true`.
- **Only Admins create, change or delete recipients.** API keys carry no role, so no API key can manage recipients any more; other users and keys can still list them (names and types) and attach them.
- **Changing subscriptions needs the Execute scope**: an API key with only the Read scope can no longer create, change, run, archive or reactivate subscriptions, or attach and detach their recipients (data contracts already needed it).
- **Failures show a generic reason.** The notification history records, for example, "the destination returned HTTP 5xx" or "connection refused by policy", never the destination's response or the connection error. Failures recorded by earlier versions may still contain the old detail.
- **API responses no longer contain destinations** except, masked, for Admins on `GET /beacon/api/recipients`: `destination` is null on subscription and data-contract details, `RecipientData.Destination` is empty from `ISubscriptionService`, and recipient search no longer matches destinations.
:::

Only Admins create, change or delete recipients; other users see recipient names and types only, and Admins see secrets masked (`********`). A masked or empty value sent back on update keeps the stored secret only while the destination stays exactly the same (scheme, host, port, path and query); otherwise the secrets must be entered again. Every destination is checked when it is saved and again before each delivery:

- Slack, Teams, Jira and webhooks need an `https` URL without user credentials.
- Slack must be `hooks.slack.com`; Teams `*.webhook.office.com`, `*.logic.azure.com` or `*.environment.api.powerplatform.com` (Power Automate workflows); Jira `*.atlassian.net` or `api.atlassian.com`.
- Webhooks may target any public host, and email any domain, unless you configure an allow-list.
- Webhook custom headers are limited to `Authorization`, `Api-Key`, `X-Api-Key`, `Ocp-Apim-Subscription-Key` and the names in `AllowedHeaders`. Headers that control the connection, routing, cookies or cloud metadata (`Host`, `Content-Length`, `X-Forwarded-*`, `Metadata-Flavor`, …) can never be added.
- Email destinations are bare addresses separated by commas; they are stored and sent as a normalised list.

Notification calls never follow redirects, speak HTTP/1.1 only, time out after 30 seconds and cap response bodies (which are never logged). By default they connect directly, and only to public addresses: Beacon resolves the host itself, refuses loopback, private (RFC 1918), CGNAT, link-local (including `169.254.169.254`), the Azure wire server (`168.63.129.16`), unique-local IPv6, everything outside IPv6 global unicast, and the IPv4 addresses carried inside mapped, NAT64 and 6to4 IPv6 addresses, and connects to the address it checked, so a DNS answer that changes after the check cannot redirect the call. Each notification type has its own allowances: an `AllowedPrivateNetworks:Jira` entry never lets a webhook reach that network. A failed delivery records a generic reason only, and is logged at Warning with the notification and recipient ids, HTTP status and exception types, never a message, URL or body.

If outbound traffic must go through a corporate proxy, set `UseSystemProxy` to `true`. Notification calls then use the system proxy (`HTTPS_PROXY`, `HTTP_PROXY`, `NO_PROXY`, or the platform's proxy settings), and Beacon checks each destination before sending instead: it resolves the host and refuses the call unless every address it resolves to is public or allowed. A destination Beacon cannot resolve is refused, so the host must still be able to resolve the notification services' names. Destinations the proxy settings bypass are connected to directly and checked as in the default mode. In proxy mode generic webhooks must be limited to `AllowedHosts:Webhook` (or Webhook disabled); the host refuses to start otherwise.

:::caution[Proxy mode does not pin the destination]
Through a proxy, Beacon's address check is advisory, not a guarantee. The proxy resolves the host name again and connects wherever its own answer points; Beacon cannot pin that connection to the address it checked. A name that resolves to a public address for Beacon and to an internal one for the proxy (or that changes in between) reaches the internal address. In proxy mode the destination guarantee therefore rests on the proxy's own resolution and egress policy: it must refuse internal, link-local and cloud-metadata destinations itself. Keep `AllowedHosts:Webhook` short, and use the default direct mode wherever the host can reach the notification services without a proxy.
:::

```json
{
  "Beacon": {
    "Notifications": {
      "AllowedHosts": {
        "Jira": [ "jira.example.internal" ],
        "Teams": [ "*.webhook.office365.us" ],
        "Webhook": [ "hooks.example.com", "*.partner.example" ],
        "Email": [ "example.com" ]
      },
      "DisabledTypes": [ "Webhook" ],
      "AllowedPrivateNetworks": {
        "Jira": [ "10.20.0.0/16", "jira.example.internal" ]
      },
      "AllowedHeaders": [ "X-Signature" ],
      "Nat64Prefixes": [ "2001:db8:64::/96" ],
      "UseSystemProxy": false,
      "RequireEncryptedSecrets": false
    }
  }
}
```

| Key | Default | Meaning |
|-----|---------|---------|
| `AllowedHosts:Slack` / `Teams` / `Jira` | empty | Extra hosts on top of the built-in vendor hosts. `*.example.com` matches any subdomain, not `example.com` itself. |
| `AllowedHosts:Webhook` | empty (any public host) | When set, the only hosts a webhook may target. Required when `UseSystemProxy` is on (unless Webhook is disabled). |
| `AllowedHosts:Email` | empty (any domain) | When set, the only domains an email recipient may use. |
| `DisabledTypes` | empty | Notification types this host does not deliver (`Email`, `Teams`, `Slack`, `Jira`, `Webhook`). |
| `AllowedPrivateNetworks:Webhook` / `Slack` / `Teams` / `Jira` | empty | IP addresses, CIDR ranges (no host bits set) or host names that calls of that type only may still reach, for example an on-premises Jira. A host-name entry never reaches link-local, metadata or Azure wire-server addresses, in any IPv6 form; only an explicit address or range does. |
| `AllowedHeaders` | empty | Webhook header names allowed on top of the built-in four. Reserved names are refused at startup. |
| `Nat64Prefixes` | empty | Extra NAT64 prefixes (IPv6 `/96`) used on the host's network, besides `64:ff9b::/96`; addresses in them are judged by the IPv4 address they carry. |
| `UseSystemProxy` | `false` | Send notification calls through the system HTTP proxy, with the destination checked before sending (see above). The proxy itself is not checked, and the proxy's own egress policy, not Beacon, decides which address each call finally reaches. |
| `RequireEncryptedSecrets` | `false` | Refuse to send to a recipient whose destination or headers are still stored unencrypted. Turn it on once you have run `EncryptStoredSecretsAsync` and it succeeded. |

A malformed entry (including a wildcard over a whole top-level domain such as `*.com`, a reserved header name, a NAT64 prefix that is not a `/96`, or a switch that is not `true` or `false`) fails the host at startup, naming the entry. The effective policy (types, host patterns, entry counts; no secrets) is logged once at startup.

Notification URLs are secrets (a Slack webhook's token is in its path). If the host traces outgoing HTTP calls with OpenTelemetry, leave notification calls out:

```csharp
.AddHttpClientInstrumentation(x => x.FilterHttpRequestMessage = y => !NotificationRequests.IsNotificationRequest(y))
```

Other APM agents (Application Insights auto-collection, Datadog, Dynatrace, …) may record full outgoing URLs, vendor webhook paths included; configure them to exclude or redact notification calls the same way.

## MCP Deployment Locks (Optional)

Most MCP behaviour is configured at runtime on the **MCP Settings** page, per project. A deployment that must guarantee a setting regardless of what an administrator clicks pins it here instead:

```json
{
  "Beacon": {
    "Mcp": {
      "ForceReadOnly": true,
      "ForceNoContentRetention": false,
      "Ceilings": {
        "MaxRowLimit": 1000,
        "StatementTimeoutSeconds": 60,
        "MaxResultBytes": 1048576,
        "MaxExplainCost": 500.0,
        "MaxConcurrentQueriesPerKey": 8,
        "MaxSqlChars": 100000,
        "MaxQuestionChars": 4000
      }
    }
  }
}
```

| Key | Effect |
|---|---|
| `ForceReadOnly` | Pins read-only enforcement on. Attempts to disable it are refused with HTTP 409. |
| `ForceNoContentRetention` | Pins the content lock on for every project — questions, SQL, and free-text errors are never persisted. See [Content retention](/features/mcp-server/#content-retention). |
| `Ceilings.*` | Clamp a numeric setting **downward only**. A ceiling can lower what an administrator configured; it can never raise it. |
| `Ceilings.MaxSqlChars` | Maximum length of SQL accepted by the MCP query tools. Default `100000`. |
| `Ceilings.MaxQuestionChars` | Maximum length of a natural-language question accepted by `ask`. Default `4000`. |

`MaxSqlChars` and `MaxQuestionChars` **always apply**: they use their defaults even when the `Ceilings` section is absent, and you can only tune them, not switch them off.

The whole section is optional — omit it and there are no locks and no deployment-set ceilings; the SQL and question length caps always apply. Every ceiling you do specify must be greater than zero, and the host fails to start otherwise, so a typo surfaces at boot rather than silently disabling a limit.

Settings resolve as **lock → project override → global value → built-in default**, with ceilings applied last. Locked fields are hidden in the UI and named in a banner; clamped fields are flagged.

## Schema Configuration

Beacon runs in the **`beacon`** schema, and only that schema. There is nothing to configure:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
    })
    .UsePostgreSql();   // connection string from ConnectionStrings:BeaconContext
```

Setting `Beacon:Schema` to anything other than `beacon` is rejected at startup:

```
Beacon only supports the 'beacon' schema, but 'tenant_a' was configured.
Remove Beacon:Schema (or set it to 'beacon') and move any existing Beacon tables into 'beacon'.
```

:::note[Why it is fixed]
Beacon ships EF Core migrations, and a migration snapshot bakes the schema in. On any other schema
EF compares model against snapshot, raises `PendingModelChangesWarning`, and `UseBeacon()`'s
`Database.Migrate()` throws before applying anything. Adding a migration cannot resolve it — the
next snapshot bakes one schema too. Rejecting the setting up front turns an opaque EF boot failure
into a message that says what to change.
:::

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

## Local Embeddings (Optional)

Beacon can run a **local ONNX embedding model in-process** to add semantic retrieval to catalog search and to the grounding context it builds for AI-generated SQL. There is no network egress — the model and tokenizer are local files you supply.

Embeddings are **disabled by default**; without them every retrieval path falls back to keyword/lexical matching rather than failing.

```json
{
  "Beacon": {
    "Embeddings": {
      "Enabled": true,
      "ModelPath": "/opt/beacon/models/bge-small-en-v1.5.onnx",
      "TokenizerPath": "/opt/beacon/models/vocab.txt"
    }
  }
}
```

| Setting | Description |
|---------|-------------|
| `Enabled` | Master switch (default `false`) |
| `ModelPath` | Path to the ONNX model file — a 384-dimension bge-small-en-v1.5 |
| `TokenizerPath` | Path to the WordPiece (BERT) vocabulary file |

Both files must exist and `Enabled` must be `true`, or the service reports itself unavailable and callers take the lexical path.

:::caution[pgvector on PostgreSQL]
On PostgreSQL, Beacon stores the vectors in a **pgvector** column with an HNSW cosine index so nearest-neighbour search runs in the database. The migration that creates it runs `CREATE EXTENSION IF NOT EXISTS vector`, so the **pgvector extension must be available on the PostgreSQL server** and the Beacon database user must be allowed to create it — otherwise migrations fail on startup. On managed Postgres this usually means enabling the extension for the instance first.

On SQL Server the same vectors are compared in memory from their byte representation; semantic retrieval works there without any extension.
:::

Indexing runs as recurring background jobs (`mcp-embedding-reindex` and `mcp-docchunk-reindex`, every 12 hours). See the [Knowledge Base guide](/features/knowledge-base/#semantic-retrieval).

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
