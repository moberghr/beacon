---
title: Hardening guide
description: "The order of work and complete, copyable configurations to take a Beacon host from a development setup to a production one and, where needed, to the regulated profile."
---

This guide is for the person who deploys a Beacon host and owns its configuration. It does not explain each protection in depth. It tells you what to decide, in what order, and gives complete files you can start from. Every protection links to the page that explains it.

There are three target levels:

| Level | Use it for | What it means |
|---|---|---|
| Development | A laptop or a test database | Defaults plus a few relaxations that exist for local work only. |
| Production | A host that real people and real data sources use | Every security setting is decided on purpose. Sign-in goes through single sign-on, browser protections are enforced, audit and retention are on. |
| Regulated | A host that must pass an audit | Production plus `Beacon:Profile = Regulated`, which fills some keys and refuses to start when a required setting is missing. |

Every value in this guide comes from .NET configuration: `appsettings.json`, environment variables or a secret store. A secret (an encryption key, a client secret, a connection string with a password) never goes in a file that is committed. The examples keep secrets out of the JSON and show the environment variables that carry them.

The settings that are not configuration keys (user management, the approval workflow, Data Protection, forwarded headers) are set in `Program.cs`. Each section says which.

## Before you start

Have these ready before you change any setting.

| Need | Why | How |
|---|---|---|
| An encryption key | Beacon encrypts data-source connection strings with it. The host refuses to start without a valid one, in every environment. | Generate base64 of 32 random bytes: `openssl rand -base64 32`. Keep it in a secret store. See [Secrets and keys](/security/secrets-and-keys/). |
| A read-only database login per data source | Beacon's SQL checks and the engine's read-only session are layers. A login that can only read is the layer that cannot be bypassed. See [SQL execution gate](/security/sql-execution-gate/). | Create the login in each database and grant `SELECT` only. |
| TLS on every data source | A production connection must authenticate the server. | PostgreSQL and MySQL: `SSL Mode=VerifyFull`. SQL Server: `Encrypt=True` without `TrustServerCertificate`. See [Data-source connections](/security/data-source-connections/). |
| A public base URL | Links, MCP discovery documents and the browser origin check use it. Without it they follow the request's `Host` header. | The https address people use, for example `https://beacon.example.com`. |
| A Data Protection key ring location and key protection | Data Protection protects the session cookie. Outside Development a persisted key ring must be encrypted at rest, or the host does not start. | Beacon stores the ring in its database through `AddBeaconDataProtection`. Protect it with a certificate or a key vault. See [Data Protection](/security/secrets-and-keys/#data-protection). |
| An identity provider application | Production turns password sign-in off. | An OpenID Connect application registration with a client id, a client secret and your tenant id. |

Generate the key once and put it in your secret store:

```bash
openssl rand -base64 32
```

## Development

A development host needs the encryption key and the database connection string from user secrets, not from the file. The file below holds only non-secret settings.

```bash
dotnet user-secrets set "Beacon:EncryptionKey" "<output of openssl rand -base64 32>"
dotnet user-secrets set "ConnectionStrings:BeaconContext" "Host=localhost;Database=beacon;Username=beacon;Password=REPLACE_ME"
```

`appsettings.Development.json`:

```json
{
  "Beacon": {
    "PublicBaseUrl": "https://localhost:7187",
    "Security": {
      "Csp": "Enforce",
      "OriginGuard": "LogOnly"
    },
    "DataSources": {
      "Transport": {
        "AllowInsecure": true
      },
      "Outbound": {
        "AllowLoopback": true
      }
    }
  }
}
```

What each relaxation is for:

| Setting | Development value | Why it is for development only |
|---|---|---|
| `Beacon:DataSources:Transport:AllowInsecure` | `true` | Lets a data source connect without authenticating its server (no TLS verification), and lifts the refusal of risky driver options and the loopback block. Outside Development the host logs a startup Warning while it is set. |
| `Beacon:DataSources:Outbound:AllowLoopback` | `true` | Lets the API connector and the Databricks client reach `localhost`. Off by default so a data source cannot be pointed at the host itself. |
| `Beacon:Security:OriginGuard` | `LogOnly` (the default) | A request from another origin is logged and let through. Fine while you test, not for a deployed host. |
| `Beacon:UserManagement:LocalLogin:Enabled` | `true` (the default) | Password sign-in is the quickest way into a local host. Production turns it off. |

## Production

Work through the groups in this order. The complete file follows the list.

1. **Public base URL and browser protections.** Set `PublicBaseUrl`, list the origins, run the origin check in `LogOnly` for a release, then move it to `Enforce`. See [Browser protections](/security/browser-protections/).
2. **Cookie and session.** `SecureAlways` on, a session lifetime and a revalidation interval you chose. See [Sign-in and sessions](/security/sign-in-and-sessions/).
3. **Sign-in.** Single sign-on on, password sign-in off, one break-glass account. See [Sign-in and sessions](/security/sign-in-and-sessions/).
4. **Limits.** Rate limits, lockout, request limits and query limits. See [Rate and request limits](/security/rate-and-request-limits/).
5. **Data sources.** Transport enforcement on stored connections and an allow-list of hosts. See [Data-source connections](/security/data-source-connections/).
6. **MCP locks, audit and retention.** See [Deployment locks](/security/deployment-locks/), [Audit trail](/security/audit-trail/) and [Data retention](/security/data-retention/).
7. **Secrets and `Program.cs`.** Environment variables, forwarded headers, timeouts, Data Protection.

### `appsettings.json`

The prose after the file explains each group, because JSON has no comments. Every value is one the code accepts. Values marked as examples are choices the host must make.

```json
{
  "Beacon": {
    "PublicBaseUrl": "https://beacon.example.com",
    "Security": {
      "Csp": "Enforce",
      "OriginGuard": "Enforce",
      "AllowedOrigins": [ "https://beacon.example.com" ]
    },
    "Authentication": {
      "Cookie": {
        "SecureAlways": true
      },
      "Session": {
        "RevalidateMinutes": 5,
        "AbsoluteLifetimeHours": 12,
        "PersistSsoCookie": false
      },
      "Oidc": {
        "Enabled": true,
        "Authority": "https://login.example.com/00000000-0000-0000-0000-000000000000/v2.0",
        "ClientId": "00000000-0000-0000-0000-000000000001",
        "AllowedTenants": [ "00000000-0000-0000-0000-000000000000" ],
        "BlockGuests": true,
        "RequireMfa": true
      }
    },
    "UserManagement": {
      "MinimumPasswordLength": 12,
      "LocalLogin": {
        "Enabled": false,
        "BreakGlassUserNames": [ "REPLACE_ME" ]
      }
    },
    "Authorization": {
      "RequireProjectMembership": true
    },
    "ApiKeys": {
      "MaxLifetimeDays": 365,
      "EnforceMaxLifetimeOnExistingKeys": true
    },
    "RateLimits": {
      "Login": { "PermitLimit": 10, "WindowSeconds": 60 },
      "Api": { "PermitLimit": 600, "WindowSeconds": 60 },
      "Anonymous": { "PermitLimit": 30, "WindowSeconds": 60 },
      "Mcp": { "PermitLimit": 60, "WindowSeconds": 60 },
      "Ask": { "PermitLimit": 10, "WindowSeconds": 60 },
      "Lockout": { "Enabled": true, "MaxFailedAttempts": 5, "DurationMinutes": 15 }
    },
    "RequestLimits": {
      "ApiMaxBodyBytes": 2097152, "McpMaxBodyBytes": 4194304, "ApiTimeoutSeconds": 300,
      "McpMaxInitializesPerCaller": 60, "McpMaxIdleSessions": 1000
    },
    "Query": {
      "MaxExecutionRows": 100000, "StatementTimeoutSeconds": 120, "MaxResultBytes": 268435456
    },
    "DataSources": {
      "AllowHostCredentials": false,
      "AllowedHosts": [ "db.internal.example.com" ],
      "Transport": {
        "EnforceOnExisting": true,
        "AllowInsecure": false
      },
      "Outbound": {
        "AllowPrivateNetworks": true,
        "AllowLoopback": false
      }
    },
    "LLM": {
      "AllowInsecureEndpoint": false
    },
    "Encryption": {
      "AllowLegacyCbc": false
    },
    "Audit": {
      "Required": true,
      "ExecutionLog": {
        "Required": true,
        "RetentionDays": 365
      }
    },
    "Retention": {
      "Days": 90,
      "ContentDays": 30,
      "Locked": false
    },
    "Mcp": {
      "ForceReadOnly": true,
      "ForcePiiDetection": true,
      "ForbidRowDataToLlm": false,
      "DataNotice": true,
      "Audit": {
        "Required": true,
        "RetentionDays": 365
      },
      "Ceilings": {
        "MaxRowLimit": 1000, "StatementTimeoutSeconds": 30,
        "MaxResultBytes": 262144, "MaxConcurrentQueriesPerKey": 4
      }
    },
    "Migrations": {
      "Enabled": false
    },
    "Embed": {
      "Enabled": false
    },
    "DataProtection": {
      "ApplicationName": "beacon-production"
    }
  }
}
```

The file leaves out `EncryptionKey`, `Audit:IntegrityKey` and `Authentication:Oidc:ClientSecret`. They come from the environment (next section).

### What each group decides

**Public base URL and browser protections.** `PublicBaseUrl` is an absolute https URL. Its origin is added to the allowed origins. `AllowedOrigins` lists every other origin browsers use. Each entry is a scheme, a host and an optional port, with no path. `OriginGuard: Enforce` refuses a state-changing request from another origin with 403. Start with `LogOnly`, read the `The Beacon origin check would refuse` warnings for a normal week, then switch. `Csp: Enforce` sends a Content-Security-Policy under `/beacon`. Details: [Browser protections](/security/browser-protections/).

**Cookie and session.** `SecureAlways` issues the `Beacon.Auth` cookie with the `Secure` flag on every request, which matters behind a proxy that terminates TLS. `RevalidateMinutes` (0 to 1440, default 5) is the longest time between checks of the user against the store: a disabled user loses the session within that time. `AbsoluteLifetimeHours` (1 to 8760, default 12) ends a session that long after sign-in however often it is renewed. `PersistSsoCookie: false` drops a single sign-on cookie when the browser closes. The values above are the defaults. Shorten the lifetime if your policy requires it. See [Sign-in and sessions](/security/sign-in-and-sessions/).

**Sign-in.** With `Oidc:Enabled` true, the host fails at startup unless `Authority`, `ClientId` and `ClientSecret` are all set, and unless `AllowedTenants` lists at least one tenant id (or `AllowAnyTenant` is true for a provider that issues no `tid` claim). With `LocalLogin:Enabled` false, the password endpoint is not mapped and only the names in `BreakGlassUserNames` (at most 3, no blank entry) can still use a password. Use one name and keep its password in a vault. Every use is logged as a Warning (event 7119). `RequireMfa: true` requires the ID token to carry an `amr` of `mfa` or an accepted `acr`. Check that your identity provider sends one of them before you turn it on, or every sign-in is refused with `?ssoError=mfa_required`.

**Project access and API keys.** `RequireProjectMembership: true` restricts every project and makes data sources no project links Admin-only. It needs user management (set in `Program.cs`), or the host fails at startup. Add members to your projects before you turn it on. `EnforceMaxLifetimeOnExistingKeys: true` applies the maximum lifetime to API keys stored without an expiry. See [Authorization](/security/authorization/) and [API keys](/security/api-keys/).

**Rate limits and lockout.** The values are the defaults. Each `PermitLimit` runs from 0 to 100000 and `0` turns that limit off. Keep `Lockout` on: it locks an internal account after repeated wrong passwords, where the login limit counts only per client address. Behind a proxy, enable forwarded headers (see `Program.cs` below) or every client shares the proxy's address. See [Rate and request limits](/security/rate-and-request-limits/).

**Request limits and query limits.** `RequestLimits` bounds body size, request time and MCP sessions. `Query` bounds the rows, time and bytes of the statements Beacon runs for its own features. Both are shown at their defaults. The code gives no stricter recommendation, so tighten them to what your data sources need. Values out of range fail startup (`Query:MaxExecutionRows` 1 to 10000000, `Query:StatementTimeoutSeconds` 1 to 3600).

**Data sources.** `Transport:EnforceOnExisting: true` makes a stored connection that does not authenticate its server stop connecting, instead of logging one Warning. Fix those connections first. `AllowedHosts` lists every host a connection may name (a host name, an IP address, or `*.example.com`). Empty means any host. `AllowHostCredentials: false` refuses options that sign in as the Beacon host. `Outbound` governs the API connector and Databricks clients: private networks are allowed by default, loopback is not. `Beacon:LLM:AllowInsecureEndpoint: false` requires https for an LLM endpoint outside loopback. See [Data-source connections](/security/data-source-connections/) and [LLM and prompt safety](/security/llm-data-safety/).

**Encryption.** `Encryption:AllowLegacyCbc: false` stops Beacon reading values in the old unauthenticated format. It defaults to on, so existing values keep working. Turn it off only when the re-encryption run reports no value left in that format. See [Secrets and keys](/security/secrets-and-keys/).

**Audit.** `Audit:Required: true` means a sign-in whose security audit event cannot be written gets no session (a break-glass sign-in is the exception). `ExecutionLog:Required: true` means a statement whose execution log row cannot be written does not run. `ExecutionLog:RetentionDays` is an example value; unset keeps rows forever. See [Audit trail](/security/audit-trail/).

**Retention.** `Days: 90` and `ContentDays: 30` are the documented recommendations. `Locked: false` here: turn the lock on when policy says no window may keep data forever. With the lock on, `Days`, every target's window, `ContentDays` and `Mcp:Audit:RetentionDays` must all be above zero, or startup fails. See [Data retention](/security/data-retention/).

**MCP locks.** `ForceReadOnly` and `ForcePiiDetection` at `true` stop an Admin turning those off at runtime. `Audit:Required: true` withholds a tool result whose audit row cannot be written. `Audit:RetentionDays: 365` is the documented recommendation. The four ceilings stop an Admin raising those limits above the built-in defaults; the values shown are the built-in defaults. `ForbidRowDataToLlm` is the host's decision: `true` keeps every value read from a data source away from the LLM provider. See [Deployment locks](/security/deployment-locks/).

**Off by default.** `Migrations:Enabled` (the one write path) and `Embed:Enabled` (the anonymous embed routes) are deployment locks. Leave them `false` unless you use the feature. Turning one on is a decision to review, and the regulated report names it (event 9132).

**Data Protection.** `DataProtection:ApplicationName` must be set outside Development when you call `AddBeaconDataProtection`. Give each environment its own name so a cookie from one is never accepted by another.

### Environment variables

Configuration keys map to environment variables with `__` for each `:`. Put these in your secret store or orchestrator, never in a committed file.

```bash
# Required: base64 of at least 32 random bytes (openssl rand -base64 32)
Beacon__EncryptionKey=<base64 of 32 random bytes>

# Keys the audit trail's integrity hashes (at least 32 characters). Required by the regulated profile.
Beacon__Audit__IntegrityKey=<at least 32 random characters>

# Single sign-on
Beacon__Authentication__Oidc__ClientSecret=<client secret from the identity provider>

# Beacon's own database
ConnectionStrings__BeaconContext=Host=db.internal.example.com;Database=beacon;Username=beacon;Password=<password>;SSL Mode=VerifyFull
```

.NET configuration does not expand `${NAME}`, so a key written that way is refused at startup. A list takes an index: `Beacon__DataSources__AllowedHosts__0=db.internal.example.com`.

### `Program.cs`

Three things are code, not configuration: the host's request pipeline, forwarded headers and Data Protection. This follows the structure of `Beacon.SampleProject`; the other calls a full host makes (more connectors, `AddBeaconOpenApi`, an LLM provider) are in [Host setup](/integrate/host-setup/). `AddMcp` needs `AddAI` before it, and `AddCookieAuth` needs your own `IClaimsTransformation` (a ready one is on that page).

```csharp
var builder = WebApplication.CreateBuilder(args);

// Kestrel keep-alive and request-header timeouts, and the default HttpClient timeout, set to 5 minutes for long AI calls.
builder.ConfigureBeaconTimeouts();

builder.Services.AddBeacon(builder.Configuration, options =>
    {
        // Your own IBeaconScheduler (the sample host's BeaconScheduler runs on Moberg.Warp).
        options.AddBeaconScheduler<BeaconScheduler>();
        options.Authorization.Enabled = true;
        options.Authentication.EnableLoginForm = true;
        options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();

        // Needed for RequireProjectMembership, per-user roles and break-glass accounts.
        options.EnableUserManagement();

        // Production: a second person approves changes. The three switches below default to false.
        // CoversMigrationJobs, CoversCustomSqlRules and CoversRecipientDestinations default to true: leave them.
        options.EnableApprovalWorkflow(x =>
        {
            x.CoversDataSourceConnections = true;
            x.CoversLlmConnection = true;
            x.CoversAdminRoleGrants = true;
        });
    })
    .AddPostgreSqlConnector()
    .AddAI()
    .AddMcp()
    .AddApi()
    .AddCookieAuth<AppClaimsTransformation>() // your IClaimsTransformation; see the host setup page
    .AddOidc()
    .UsePostgreSql();

// Data Protection: the key ring lives in Beacon's database under Beacon:DataProtection:ApplicationName.
// Outside Development the keys must be encrypted at rest, or the host does not start.
var certificate = X509CertificateLoader.LoadPkcs12FromFile(
    "/etc/beacon/dp-cert.pfx", builder.Configuration["YourApp:DataProtectionCertPassword"]); // your own key, from a secret store

builder.Services.AddBeaconDataProtection(builder.Configuration, builder.Environment)
    .ProtectKeysWithCertificate(certificate);

var app = builder.Build();

// Behind a reverse proxy: honour the forwarded address and scheme from your own proxies only.
// Without this the per-address limits see the proxy, and the origin check sees http.
var forwardedOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
};
forwardedOptions.KnownProxies.Clear();
forwardedOptions.KnownNetworks.Clear();
forwardedOptions.KnownProxies.Add(IPAddress.Parse("10.0.0.10")); // your proxy
app.UseForwardedHeaders(forwardedOptions);

app.UseHttpsRedirection();

// Security headers on Beacon's routes, before static files so the SPA's files carry them.
app.UseBeaconSecurityHeaders();
app.UseStaticFiles();

// The whole Beacon pipeline in its required order. Call it once.
app.UseBeaconPipeline();

app.MapBeacon();
app.Run();
```

Notes on this code:

- `ConfigureBeaconTimeouts` is host-wide. Beacon never applies it on its own, so a host must call it.
- Any `ProtectKeysWith*` call (for example `ProtectKeysWithAzureKeyVault`) works in place of the certificate.
- The sample host reads `Beacon:ForwardedHeaders:Enabled` and `Beacon:ForwardedHeaders:KnownProxies` from configuration to decide whether to add the forwarded-headers block. Those two keys belong to the host, not to Beacon. Beacon itself reads no forwarded-headers setting.
- HSTS is the host's. Beacon does not send `Strict-Transport-Security`; add `app.UseHsts()` for production.
- The Regulated profile does not check forwarded headers. See [Rate and request limits](/security/rate-and-request-limits/).

## Regulated

`Beacon:Profile = Regulated` is a check, not a different mode. It does two things:

1. **Fills** the keys in the next table when you leave them unset.
2. **Refuses to start**, listing every failure at once, when any required setting is weaker than the profile allows. It never overrides a value you set.

The full requirement list and its reasoning is on [Regulated profile](/security/regulated-profile/). This section gives what you need to build a passing file.

### Keys the profile fills

You may leave these out of the file. They come from `RegulatedProfile.DefaultedKeys`.

| Key | Filled value |
|---|---|
| `Beacon:Mcp:Audit:Required` | `true` |
| `Beacon:Mcp:ForbidRowDataToLlm` | `true` |
| `Beacon:Retention:Locked` | `true` |
| `Beacon:Mcp:Ceilings:MaxRowLimit` | `1000` |
| `Beacon:Mcp:Ceilings:StatementTimeoutSeconds` | `30` |
| `Beacon:Mcp:Ceilings:MaxResultBytes` | `262144` |
| `Beacon:Mcp:Ceilings:MaxConcurrentQueriesPerKey` | `4` |
| `Beacon:Audit:Required` | `true` |
| `Beacon:Audit:ExecutionLog:Required` | `true` |
| `Beacon:Authentication:Cookie:SecureAlways` | `true` |
| `Beacon:UserManagement:MinimumPasswordLength` | `12` |

### Keys the host must set

The profile never fills these, because they change how people work or need a secret. A file without them does not start.

| Setting | Required value |
|---|---|
| `Beacon:UserManagement:LocalLogin:Enabled` | `false` (list emergency accounts in `BreakGlassUserNames`) |
| `Beacon:Authentication:Oidc:RequireMfa` | `true` when `Oidc:Enabled` is true |
| `Beacon:Authorization:RequireProjectMembership` | `true` |
| `Beacon:ApiKeys:EnforceMaxLifetimeOnExistingKeys` | `true` |
| `Beacon:Security:OriginGuard` | `Enforce` |
| `Beacon:PublicBaseUrl` | an absolute `https` URL |
| `Beacon:Audit:IntegrityKey` | set (at least 32 characters) |
| `Beacon:Encryption:AllowLegacyCbc` | `false`, written explicitly |
| `Beacon:DataSources:Transport:EnforceOnExisting` | `true` |
| `Beacon:DataSources:AllowedHosts` | at least one host |
| `EnableUserManagement()` in code | called |
| `EnableApprovalWorkflow()` in code | called, with `CoversDataSourceConnections`, `CoversLlmConnection` and `CoversAdminRoleGrants` set to `true`. `CoversMigrationJobs`, `CoversCustomSqlRules` and `CoversRecipientDestinations` are also required; they default to `true`, so leave them. `AllowSelfApproval` stays `false`. |

Because `Retention:Locked` is filled, `Retention:Days`, `Retention:ContentDays` and `Mcp:Audit:RetentionDays` must be above zero. These are separate startup failures from the profile's own.

Do not weaken these defaults: `Mcp:ForceReadOnly`, `Mcp:ForcePiiDetection` and `Security:Csp` stay on, `Transport:AllowInsecure`, `AllowHostCredentials`, `LLM:AllowInsecureEndpoint` and `DataProtection:AllowUnencryptedKeys` stay off, every `PermitLimit` stays above zero, and `AllowSelfApproval` stays `false`.

### A complete `appsettings.json` that passes

This file meets every requirement in `RegulatedProfile.Assess` for a host that uses single sign-on and calls `EnableUserManagement` and `EnableApprovalWorkflow` in code (see the `Program.cs` above). The keys the profile fills are left out on purpose, so the report shows what it filled.

```json
{
  "Beacon": {
    "Profile": "Regulated",
    "PublicBaseUrl": "https://beacon.example.com",
    "Security": {
      "Csp": "Enforce",
      "OriginGuard": "Enforce",
      "AllowedOrigins": [ "https://beacon.example.com" ]
    },
    "Authentication": {
      "Session": {
        "RevalidateMinutes": 5,
        "AbsoluteLifetimeHours": 12
      },
      "Oidc": {
        "Enabled": true,
        "Authority": "https://login.example.com/00000000-0000-0000-0000-000000000000/v2.0",
        "ClientId": "00000000-0000-0000-0000-000000000001",
        "AllowedTenants": [ "00000000-0000-0000-0000-000000000000" ],
        "BlockGuests": true,
        "RequireMfa": true
      }
    },
    "UserManagement": {
      "LocalLogin": {
        "Enabled": false,
        "BreakGlassUserNames": [ "REPLACE_ME" ]
      }
    },
    "Authorization": {
      "RequireProjectMembership": true
    },
    "ApiKeys": {
      "EnforceMaxLifetimeOnExistingKeys": true
    },
    "Encryption": {
      "AllowLegacyCbc": false
    },
    "DataSources": {
      "AllowedHosts": [ "db.internal.example.com" ],
      "Transport": {
        "EnforceOnExisting": true
      }
    },
    "Retention": {
      "Days": 90,
      "ContentDays": 30
    },
    "Mcp": {
      "Audit": {
        "RetentionDays": 365
      }
    },
    "DataProtection": {
      "ApplicationName": "beacon-production"
    }
  }
}
```

The secrets come from the environment, as in production: `Beacon__EncryptionKey`, `Beacon__Audit__IntegrityKey`, `Beacon__Authentication__Oidc__ClientSecret` and the connection string. `Beacon:Audit:IntegrityKey` is a requirement, so the host does not start without it.

If you set a key the profile fills, your value is kept. A weaker value then fails startup. For example, `"Audit": { "Required": false }` is refused, because the profile never overrides a configured value.

### What a missed requirement looks like

The profile is validated at startup. Every failed requirement becomes one line, and all lines are reported together, so you fix the list once. Each line reads:

```text
Beacon:Profile is Regulated, so <setting> <requirement>.
```

A host that omits the origin guard, the public base URL, the integrity key and the allowed hosts fails with:

```text
Microsoft.Extensions.Options.OptionsValidationException:
Beacon:Profile is Regulated, so Beacon:Security:OriginGuard must be Enforce (list the origins browsers use in Beacon:Security:AllowedOrigins first): a request from another origin is only logged.;
Beacon:Profile is Regulated, so Beacon:PublicBaseUrl must be set to Beacon's https address: links, discovery documents and the origin check otherwise follow the request's Host header.;
Beacon:Profile is Regulated, so Beacon:Audit:IntegrityKey must be set, from a secret store: the audit trail is otherwise signed with a key derived from the encryption key, which changes with it.;
Beacon:Profile is Regulated, so Beacon:DataSources:AllowedHosts must list the hosts data sources may reach: a connection can reach any host on the Beacon host's networks.
```

A code-only requirement names no configuration key, for example `ApprovalWorkflow.CoversAdminRoleGrants must be true (in code): a user is given the Admin role without a second person's approval`. A value you set is never echoed. In gateway mode (`AddBeaconMcpGateway`) the `ApprovalWorkflow.` requirements are not judged, and every other group still is.

To move an existing host to the profile, start with the profile unset, read the startup report (next section), fix group by group in a test environment, then promote. The order is on [Regulated profile](/security/regulated-profile/#migrating-an-existing-deployment).

## Verify the result

Check each protection after a deploy. Do not assume the file took effect.

### Startup log

Look at the log of the first start.

| Event | Level | Meaning |
|---|---|---|
| 9130 `DeploymentProfileWeakSettings` | Warning | One or more settings are weaker than the regulated profile requires. The text lists each setting with its requirement. Without a profile it names only the original group: the MCP locks and ceilings, the retention lock, the request limits, the lockout, local login and MFA. |
| 9131 `DeploymentProfileHolds` | Information | Profile is `Regulated` and every requirement holds. Lists the keys the profile filled, or `none`. This is the line you want. |
| 9132 `DeploymentProfileSurfacesOn` | Information | Profile is `Regulated`, every requirement holds, and `Beacon:Embed:Enabled` or `Beacon:Migrations:Enabled` is on. Review that you need them. |
| 9133 `DeploymentProfileOtherWeakSettings` | Information | Without the profile: the settings beyond the original group that are weaker than the profile requires. |

With the profile unset and every setting strong, none of these events is written. The production file above leaves `Retention:Locked` and `Mcp:ForbidRowDataToLlm` at `false`, both in the original group, so that host logs one 9130 Warning naming them until you decide otherwise. To see the full verdict, set the profile in a test environment and read 9131.

Other startup lines that point at a setting from this guide. They carry no event id; search the log for the text:

| Text starts with | Means |
|---|---|
| `Beacon:Security:OriginGuard is Enforce but no origin is configured` | `OriginGuard` is `Enforce` without `AllowedOrigins` or `PublicBaseUrl`. |
| `Beacon:PublicBaseUrl is not an absolute http or https URL` | `PublicBaseUrl` is a bare host name or another scheme. |
| `The server listens on plain HTTP outside Development, no forwarded scheme is configured and Beacon:Authentication:Cookie:SecureAlways is off` | The session cookie can be issued without `Secure`. Set `SecureAlways` or honour `X-Forwarded-Proto`. |
| `Beacon:Mcp:ForceReadOnly is off` and `Beacon:Mcp:ForcePiiDetection is off` | An MCP lock was lowered. |
| `Stored MCP settings contradict the deployment locks` | A stored Admin setting is overridden by a lock. Informational. |
| `Data sources <ids> have no database-level read-only enforcement` | Some engines rely on the parser gates and a read-only login. See the badge below. |

### Security headers

Request the SPA root and read the response headers:

```bash
curl -sI https://beacon.example.com/beacon
```

Expect these headers on any response under `/beacon`, a redirect to the login page included:

```text
x-content-type-options: nosniff
x-frame-options: DENY
referrer-policy: strict-origin
permissions-policy: accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()
content-security-policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'self'; worker-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'
```

With `Csp` set to `ReportOnly`, the last header is `content-security-policy-report-only`. With `Off` it is absent. The CSP is sent only under `/beacon`; the other headers are sent on every Beacon route. `strict-transport-security` is not in this list because HSTS is the host's (`UseHsts`). Header names may arrive in lower case, which is normal.

### Origin check

Send a state-changing request that claims to come from another origin:

```bash
curl -si -X POST https://beacon.example.com/beacon/api/projects \
  -H 'Origin: https://evil.example.org' \
  -H 'Content-Type: application/json' \
  -d '{}'
```

The check runs before the route is matched, so any path under `/beacon` gives the same answer. Under `OriginGuard = Enforce` the answer is:

```http
HTTP/1.1 403 Forbidden
Content-Type: application/problem+json

{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.4",
  "title": "Forbidden",
  "status": 403,
  "detail": "The request's origin is not allowed.",
  "traceId": "..."
}
```

The log line is a Warning with no event id: `The Beacon origin check refused POST /beacon/api/projects (ForeignOrigin).` Under `LogOnly` the request goes on and the log line reads `The Beacon origin check would refuse ... the request was let through because Beacon:Security:OriginGuard is LogOnly.`

Repeat the request with `-H 'Origin: https://beacon.example.com'`. It must not answer 403 with that detail. It goes on to the next stage (without a session, expect a 401), which proves the origin you own is allowed. A request with an `Authorization` header, or with no `Origin` and no `Sec-Fetch-Site` header, is not judged by this check: it is not a cookie-authenticated browser request.

### Read-only enforcement badge

Open each data source in the UI. A source whose engine does not refuse writes in Beacon's read-only session shows a **Read-only login required** badge. PostgreSQL and MySQL show none, because the engine refuses writes. SQL Server, Synapse, Snowflake, BigQuery and Databricks show it: for those, Beacon's SQL checks are one guard, and the other must be a login that can only read. Confirm that every data source with the badge uses such a login. A **Migration write target** badge marks a source an Admin chose as a migration destination, which Beacon never reads.

## Checklist

One row per decision. Tick it when the setting is in place and verified.

| Item | Setting or action | Page |
|---|---|---|
| Encryption key | `Beacon__EncryptionKey` from `openssl rand -base64 32`, in a secret store | [Secrets and keys](/security/secrets-and-keys/) |
| Audit integrity key | `Beacon__Audit__IntegrityKey`, at least 32 characters, in a secret store | [Secrets and keys](/security/secrets-and-keys/) |
| Data Protection | `AddBeaconDataProtection` with `ProtectKeysWith*`, an `ApplicationName` per environment | [Secrets and keys](/security/secrets-and-keys/#data-protection) |
| Read-only logins | One login per data source that can only read | [SQL execution gate](/security/sql-execution-gate/) |
| Data-source TLS | `VerifyFull` or the engine's equivalent; `Transport:EnforceOnExisting` true | [Data-source connections](/security/data-source-connections/) |
| Host allow-list | `Beacon:DataSources:AllowedHosts` lists every host | [Data-source connections](/security/data-source-connections/) |
| Public base URL | `Beacon:PublicBaseUrl` set to the https address | [Browser protections](/security/browser-protections/) |
| Origin check | `OriginGuard` `Enforce` after a clean `LogOnly` release; `AllowedOrigins` listed | [Browser protections](/security/browser-protections/) |
| Session cookie | `Cookie:SecureAlways` true | [Sign-in and sessions](/security/sign-in-and-sessions/) |
| Single sign-on | `Oidc:Enabled`, `AllowedTenants`, `BlockGuests`, `RequireMfa` after checking `amr` or `acr` | [Sign-in and sessions](/security/sign-in-and-sessions/) |
| Password sign-in | `LocalLogin:Enabled` false, one `BreakGlassUserNames` entry | [Sign-in and sessions](/security/sign-in-and-sessions/) |
| Project membership | `RequireProjectMembership` true, user management on in code | [Authorization](/security/authorization/) |
| API key lifetime | `ApiKeys:EnforceMaxLifetimeOnExistingKeys` true | [API keys](/security/api-keys/) |
| Rate limits and lockout | Each `PermitLimit` above 0; `Lockout` on | [Rate and request limits](/security/rate-and-request-limits/) |
| Forwarded headers | `UseForwardedHeaders` with your own proxies only | [Rate and request limits](/security/rate-and-request-limits/) |
| MCP locks | `ForceReadOnly`, `ForcePiiDetection` true; ceilings set | [Deployment locks](/security/deployment-locks/) |
| MCP audit | `Mcp:Audit:Required` true; `RetentionDays` 365 | [Deployment locks](/security/deployment-locks/) |
| Row data and the LLM | `Mcp:ForbidRowDataToLlm` decided; `LLM:AllowInsecureEndpoint` false | [LLM and prompt safety](/security/llm-data-safety/) |
| Security audit | `Audit:Required` and `ExecutionLog:Required` true | [Audit trail](/security/audit-trail/) |
| Retention | `Retention:Days` 90, `ContentDays` 30, `Locked` by decision | [Data retention](/security/data-retention/) |
| Approval workflow | `EnableApprovalWorkflow` with the three `Covers*` switches in code | [Regulated profile](/security/regulated-profile/) |
| Regulated profile | `Beacon:Profile` `Regulated`; event 9131 in the log | [Regulated profile](/security/regulated-profile/) |
| Timeouts | `ConfigureBeaconTimeouts()` in `Program.cs` | [Production checklist](/integrate/production/) |

## Related pages

- [Regulated profile](/security/regulated-profile/)
- [Deployment locks](/security/deployment-locks/)
- [Secrets and keys](/security/secrets-and-keys/)
- [Sign-in and sessions](/security/sign-in-and-sessions/)
- [Browser protections](/security/browser-protections/)
- [Rate and request limits](/security/rate-and-request-limits/)
- [Data-source connections](/security/data-source-connections/)
- [Data retention](/security/data-retention/)
- [Security model](/security/)
- [LLM and prompt safety](/security/llm-data-safety/)
- [Production checklist](/integrate/production/)
- [Configuration reference](/getting-started/configuration/)
