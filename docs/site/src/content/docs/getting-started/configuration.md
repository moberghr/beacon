---
title: Configuration
description: Every configuration key Beacon reads, grouped by area with type, default and validation; the code-only options set in AddBeaconServices; the deployment locks; and the environment-variable form.
---

This is the reference for everything you can configure in Beacon. It applies whether you run the `Beacon.SampleProject` host or embed the `Beacon.*` packages in your own ASP.NET Core application; [Host wiring](/integrate/host-setup/) shows the registration calls in context.

## How configuration is read

Beacon has two kinds of settings:

1. **Configuration keys**, read from `IConfiguration`: `appsettings*.json`, environment variables, user secrets or a secrets provider. Everything under [Core and database](#core-and-database) through [Telemetry](#telemetry) below.
2. **Code-only options**, set in the `AddBeaconServices(configuration, options => …)` lambda. They are not read from configuration. See [Code-only options](#code-only-options).

Rules that apply throughout:

- **Validated at startup.** Most sections are validated when the host starts; a bad value stops the host with a message that names the key (never a secret's value). Sections marked "validated at first use" are checked when the feature first reads them.
- **Secrets never go in `appsettings.json`.** Supply `Beacon:EncryptionKey`, connection strings, the OIDC client secret and LLM keys through environment variables or a secrets provider. .NET configuration does not expand `${...}` placeholders: the text itself would become the value (for `Beacon:EncryptionKey` and `Beacon:Mcp:CallerHashKey` startup refuses it).
- **The sample host.** `src/Beacon.SampleProject/appsettings.json` holds only `Logging`; the sample's Beacon settings are in `appsettings.Development.json`, which loads only with `ASPNETCORE_ENVIRONMENT=Development`. Outside Development, supply every value yourself.

### Environment-variable form

Replace each `:` with a double underscore. List items take an index; dictionary keys are path segments.

```bash
export ConnectionStrings__BeaconContext="Host=db;Database=beacon;Username=beacon;Password=…"
export Beacon__EncryptionKey="<base64 of 32 random bytes>"
export Beacon__PreviousEncryptionKeys__0="<the key used until now>"
export Beacon__UserManagement__LocalLogin__BreakGlassUserNames__0="superadmin"
export Beacon__Query__MaxExecutionRows=50000
export Beacon__Mcp__ForceReadOnly=true
```

An Azure Key Vault secret named `Beacon--EncryptionKey` maps to `Beacon:EncryptionKey` the same way.

## Host setup in Program.cs

A condensed version of the sample host's wiring. The SPA is served at **`/beacon`**, so cookie authentication, the login and setup endpoints and the middlewares take that base path.

```csharp
using Beacon.AI;
using Beacon.Api;
using Beacon.Api.Authentication;
using Beacon.Api.Endpoints;
using Beacon.Core;
using Beacon.Core.PostgreSql;
using Beacon.MCP;
using Beacon.MCP.Discovery;
using Beacon.UI;

builder.Services.AddBeaconHostInfrastructure<MyClaimsTransformation>();

builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();        // required: your IBeaconScheduler
        options.BaseUrl = builder.Configuration["Beacon:BaseUrl"];
        options.UseAI = true;
        options.AddEmailAdapter<MyEmailAdapter>();
        options.Authorization.Enabled = true;
        options.Authentication.EnableLoginForm = true;
        options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();
        options.EnableUserManagement();
    })
    .AddPostgreSqlConnector()
    .AddSqlServerConnector()
    // … other connectors …
    .UsePostgreSql();                                         // ConnectionStrings:BeaconContext, schema "beacon"

builder.Services.AddBeaconCookieAuthentication(BeaconUiEndpointRouteBuilderExtensions.BasePath); // "/beacon"
builder.Services.AddBeaconDataProtection(builder.Configuration, builder.Environment);
builder.Services.AddBeaconOidcAuthentication(builder.Configuration);   // optional SSO
builder.Services.AddBeaconAI(builder.Configuration);
builder.Services.AddBeaconMcp();
builder.Services.AddOpenApi();
builder.Services.AddAntiforgery(x =>
{
    x.HeaderName = "X-XSRF-TOKEN";
    x.Cookie.Name = ".Beacon.Antiforgery";
});
builder.Services.AddBeaconApiAuthorization();
builder.Services.AddBeaconApiServices();

var app = builder.Build();
var beaconConfiguration = app.Services.GetRequiredService<BeaconConfiguration>();

app.UseStaticFiles();
app.UseMcpResourceMetadataChallenge();
app.UseBeaconAuthentication();                               // API key → bearer JWT → UseAuthentication → Beacon.Auth cookie
app.UseAuthorization();
app.UseMiddleware<LoginFormAuthMiddleware>(beaconConfiguration, BeaconUiEndpointRouteBuilderExtensions.BasePath);  // with the login form
app.UseMiddleware<FirstRunSetupMiddleware>(beaconConfiguration, BeaconUiEndpointRouteBuilderExtensions.BasePath); // with user management
app.UseAntiforgery();

app.MapBeaconOpenApi();                                      // /openapi/v1.json
app.MapBeaconApi();                                          // /beacon/api/* and the hub /beacon/api/hub
app.MapLoginEndpoints("/beacon", beaconConfiguration);       // /beacon/api/auth/login, logout, sso
app.MapSetupEndpoints("/beacon");                            // /beacon/api/setup/* (with user management)
app.MapBeaconMcp();                                          // /beacon/mcp, Execute-scope policy
app.MapMcpDiscovery();                                       // /.well-known/* (anonymous)
app.MapBeaconUi();                                           // the React SPA at /beacon
```

The middleware order is load-bearing; keep it. `UseBeaconAuthentication()` takes the place of `UseAuthentication()` and wires Beacon's half of it; the Beacon middlewares act on `/beacon` and [`Beacon:Authentication:AdditionalPaths`](#where-beacon-credentials-apply) only. Beacon does **not** create or migrate its own schema when the host starts: apply its migrations as described in [Metadata database](/integrate/database/).

| Path | What |
|---|---|
| `/beacon` | the React UI (`/beacon/login`, `/beacon/setup`, …) |
| `/beacon/api/*` | REST API; `/beacon/api/health` is anonymous |
| `/beacon/api/hub` | SignalR hub |
| `/beacon/mcp` | MCP server |
| `/.well-known/oauth-protected-resource`, `/.well-known/mcp/server-card.json` | MCP discovery (anonymous) |
| `/openapi/v1.json` | OpenAPI document (open in Development; signed-in callers only elsewhere unless `Beacon:OpenApi:Anonymous` is `true`) |
| `/warp` | Warp dashboard (sample host, Admin only; listed in `Beacon:Authentication:AdditionalPaths`) |

## Code-only options

Set in the `AddBeaconServices` lambda. None of these are read from configuration, except the two noted.

| Option | Default | What it does |
|---|---|---|
| `AddBeaconScheduler<T>()` | none, **required** | Your `IBeaconScheduler`; startup fails without one (see [Scheduling](#scheduling)) |
| `AddEmailAdapter<T>()` | none | Your `IEmailAdapter`; Beacon ships no email implementation (see [Email adapter](#email-adapter)) |
| `BaseUrl` | `null` | The address put in notification links: `{BaseUrl}/notifications/{id}`. Include the `/beacon` path (`https://beacon.example.com/beacon`). Not read from configuration by Beacon; the sample host reads `Beacon:BaseUrl` into it. Admin settings can replace it at runtime (see [Base URL](#base-url-configuration)) |
| `UseAI` | `false` | Turns on the AI features; with `false` they are registered as stand-ins that report AI as unavailable |
| `Authorization.Enabled` | `false` | Enforces global roles on `/beacon/api` (see [Authorization](/security/authorization/)) |
| `AddAuthorizationProvider<T>()` | `DefaultAuthorizationProvider` (allows all); `DatabaseAuthorizationProvider` with user management | The global-permission provider |
| `Authentication.EnableLoginForm` | `false` | The login form and the login redirect |
| `AddAuthenticationProvider<T>()` | none | How a login-form password is checked (`DatabaseAuthenticationProvider`, `JwtExternalApiAuthenticationProvider`, `HybridAuthenticationProvider` or your own) |
| `Authentication.LoginRedirectPath` | `/beacon/home` | Where a sign-in lands (a full browser path) |
| `Authentication.LoginPath` | `/login` | The login page, under the base path |
| `Authentication.CookieExpirationHours` | `24` | Requested session-cookie lifetime; capped by `Beacon:Authentication:Session:AbsoluteLifetimeHours` (default 12) |
| `Authentication.RememberMeExpirationDays` | `30` | Requested "remember me" lifetime; capped the same way |
| `UserManagement.Enabled` (`EnableUserManagement()`) | `false` | Beacon's user store, Users page and first-run setup |
| `UserManagement.AllowInternalUsers` | `true` | Allow password users |
| `UserManagement.MinimumPasswordLength` | `8` | Shortest password |
| `UserManagement.RequirePasswordComplexity` | `true` | Upper case, lower case, digit and another character required |
| `UserManagement.SetupToken` | unset | Also bound from `Beacon:UserManagement:SetupToken` (see [User management](#user-management-and-first-run-setup)) |
| `UserManagement.LocalLogin` | on | Also bound from `Beacon:UserManagement:LocalLogin` |
| `ApprovalWorkflow.*` (`EnableApprovalWorkflow(…)`) | off | The four-eyes workflow (see [Approval workflow](#approval-workflow)) |
| `SavedQueryTools.NamedToolLimit` | `25` | Up to this many saved-query MCP tools per caller are listed as their own `q_<name>` tools; above it, callers get `search_saved_queries` and `run_saved_query`. Must not be negative |

Bearer-token and external-login settings are code-only too, through `builder.Services.AddBeaconJwtAuthentication(jwt => …)` (see [JWT bearer](#jwt-bearer)).

## Core and database

| Key | Type | Default | What it does |
|---|---|---|---|
| `ConnectionStrings:BeaconContext` | string | **required** | Beacon's own database (PostgreSQL or SQL Server), read by `.UsePostgreSql()` / `.UseSqlServer()` without arguments. Missing: startup fails |
| `ConnectionStrings:WarpContext` | string | falls back to `BeaconContext` | Sample host: the Moberg.Warp job store, PostgreSQL only, `warp` schema. When Beacon runs on SQL Server, point it at a PostgreSQL database |
| `ConnectionStrings:{Name}` | string | none | The connection of a host-managed data source, which stores only the name and reads the string at call time |
| `Beacon:Schema` | string | `beacon` | **Not configurable.** Any value other than `beacon` stops the host (see below). Leave it out |
| `Beacon:BaseUrl` | string | none (the sample host falls back to `https://localhost:7187`) | Read by the sample host into `options.BaseUrl` |
| `Beacon:PublicBaseUrl` | string (origin) | derived from the request | The origin advertised in the MCP discovery documents and the `WWW-Authenticate` challenge. Unset, it follows the request's `Host` header, which the client controls, and startup logs a Warning. Set it behind any proxy |
| `Beacon:OpenApi:Anonymous` | bool | `false` | Publishes the OpenAPI document (`MapBeaconOpenApi()`) to anonymous callers outside Development. Off, it needs a signed-in Beacon caller; list `/openapi` in `Beacon:Authentication:AdditionalPaths` so the Beacon session reaches it. |
| `Beacon:ForwardedHeaders:Enabled` | bool | `false` | Sample host: apply `X-Forwarded-For` and `X-Forwarded-Proto` from known proxies |
| `Beacon:ForwardedHeaders:KnownProxies` | string[] | empty | Sample host: proxy IP addresses to trust; entries that are not IP addresses are skipped, and an empty list trusts none |
| `AllowedHosts` | string | None | Standard ASP.NET Core host filtering |
| `Logging:*` | n/a | n/a | Standard logging. For JSON console logs with trace ids: `Logging:Console:FormatterName=json`, `Logging:Console:FormatterOptions:IncludeScopes=true` |

### Schema configuration

Beacon runs in the **`beacon`** schema and only that schema. Its migration snapshots bake the schema in, so any other schema would make `Database.Migrate()` fail with `PendingModelChangesWarning`; Beacon refuses the setting up front:

```
Beacon only supports the 'beacon' schema, but 'tenant_a' was configured.
Remove Beacon:Schema (or set it to 'beacon') and move any existing Beacon tables into 'beacon'.
```

`.UsePostgreSql(connectionString, schema)` and `.UseSqlServer(connectionString, schema)` accept a schema argument for the same reason only as `beacon`. On PostgreSQL the search path is `beacon,public`, so a pgvector extension installed in `public` resolves.

### Forwarded headers

The forwarded-headers keys are read by the **sample host** only. A host embedding the packages calls `UseForwardedHeaders` itself. Behind a proxy this matters: the login and setup throttles key on the client address, and `SameAsRequest` cookie security keys on the scheme. Without forwarded headers the cookie is issued without the `Secure` flag behind a TLS-terminating proxy: honour the forwarded scheme, or set `Beacon:Authentication:Cookie:SecureAlways` to `true` (default `false`; the cookie is then never sent over plain HTTP, so sign-in needs HTTPS end to end or a proxy that terminates it). Outside Development, Beacon logs a Warning at startup when the server listens on a plain HTTP address and the option is off. Add HTTPS redirection and HSTS in the host.

```json
{
  "Beacon": {
    "ForwardedHeaders": {
      "Enabled": true,
      "KnownProxies": [ "10.0.0.5" ]
    }
  }
}
```

### Base URL configuration

`BaseUrl` is the address of the Beacon UI in outgoing links (Teams, Slack, webhooks: `{BaseUrl}/notifications/{id}`). The UI lives under `/beacon`, so include it:

```json
{ "Beacon": { "BaseUrl": "https://beacon.example.com/beacon" } }
```

At startup the base URL is what the host's code sets (`options.BaseUrl`). Saving **Base URL** in [Admin settings](/security/admin-settings/) replaces it in the running process at once; after a restart the startup value applies again until the settings are saved. Keep the two in step.

## Encryption key configuration (required)

| Key | Type | Default | What it does |
|---|---|---|---|
| `Beacon:EncryptionKey` | string (base64) | **required** | Encrypts data-source connection data, repository tokens, sensitive settings and recipient destinations at rest (AES-256-GCM, per-value nonce; the AES key is the SHA-256 of the key text). Checked in every environment |
| `Beacon:PreviousEncryptionKeys` | string[] | empty | Older keys, used only to decrypt |
| `Beacon:Mcp:CallerHashKey` | string | derived from the encryption key | Secret behind the MCP caller hashes in the audit; keeps them stable across a key change |

Generate a key with:

```bash
openssl rand -base64 32
```

The key must be **base64 that decodes to at least 32 random bytes**: a passphrase, a short value, base64 of text (every decoded byte printable) or of a repeated pattern (fewer than 16 distinct byte values) is refused. A published sample value and a value containing `${` are refused too. The message never contains the key.

```bash
# Development
dotnet user-secrets --project src/Beacon.SampleProject set "Beacon:EncryptionKey" "$(openssl rand -base64 32)"
# Elsewhere
export Beacon__EncryptionKey="<the generated value>"
```

API keys are not encrypted: they are SHA-256-hashed, and the raw key is shown once.

### Replacing the key

Values are always written with `Beacon:EncryptionKey`. To replace a key, set the new one and list the old one in `Beacon:PreviousEncryptionKeys` (as a list: `Beacon__PreviousEncryptionKeys__0`; a single value is read as one key, with a startup Warning). Previous keys only decrypt and are not strength-checked, since their purpose is to retire a weak key.

```bash
export Beacon__EncryptionKey="<the new value>"
export Beacon__PreviousEncryptionKeys__0="<the key configured until now>"
```

`IStoredValueReEncryptionService` re-encrypts every stored value only a previous key decrypts (conditional writes, so a concurrent save wins). The sample host enqueues it as `ReEncryptStoredValuesJob` at every start while a previous key is listed; a host with its own jobs does the same. Startup also logs how many values remain under a previous key, how many no key decrypts, and how many recipient approval hashes cannot be recomputed. Remove the previous keys once none remain.

Before changing the key, set `Beacon:Mcp:CallerHashKey` to the key in use now, so MCP audit rows keep grouping together across the change. The MCP audit's content hashes (`ContentHash`, `SqlHash`) come from the same secret and behave the same way. **Set `Beacon:Audit:IntegrityKey` before you change the key** too, so security audit events written from then on do not depend on it; events signed before keep verifying while their secret stays configured (see [Security audit trail](#security-audit-trail)).

## Data Protection (the keys behind the session cookie)

ASP.NET Core Data Protection signs and encrypts the `Beacon.Auth` cookie and the antiforgery tokens. It belongs to the **host**: `AddBeaconCookieAuthentication` configures none of it.

| Key | Type | Default | What it does |
|---|---|---|---|
| `Beacon:DataProtection:ApplicationName` | string | `Beacon` in Development; **required** elsewhere | Used by `AddBeaconDataProtection(configuration, environment)`, which keeps the key ring in the Beacon database (`data_protection_keys`). Give each deployment its own name (`beacon-production`, `beacon-uat`) so a cookie from one is refused by another. Missing outside Development: startup fails |
| `Beacon:DataProtection:AllowUnencryptedKeys` | bool | `false` | Acknowledges a key ring persisted without encryption outside Development (a startup Warning instead of a refusal) |

- **Keys encrypted at rest.** Outside Development the host does not start when the key ring is persisted without key encryption. Chain `ProtectKeysWithAzureKeyVault(...)` or `ProtectKeysWithCertificate(...)` with a key only that environment holds, or acknowledge another protection with `AllowUnencryptedKeys`.
- **Keys written before encryption are revoked.** At each start outside Development with key encryption on, keys still stored unencrypted are revoked and replaced (`DataProtectionKeyRing`, 7121); their users sign in again once.
- **No repository configured** gets a startup Warning outside Development.
- **Your own setup.** Call `AddDataProtection()` with your own application name, `PersistKeysTo*` and `ProtectKeysWith*`, and skip `AddBeaconDataProtection`.

```csharp
builder.Services.AddBeaconDataProtection(builder.Configuration, builder.Environment)
    .ProtectKeysWithAzureKeyVault(new Uri(builder.Configuration["DataProtection:KeyUri"]!), new DefaultAzureCredential());
```

**After restoring a Beacon database into another environment:** give every user a new security stamp (sessions signed in before end), delete the copied key rows so the copy creates its own keys, and if a copy may have been exposed, revoke the source environment's keys too.

```sql
-- PostgreSQL
UPDATE beacon.users SET security_stamp = md5(random()::text || clock_timestamp()::text || id::text);
-- SQL Server
UPDATE beacon.Users SET SecurityStamp = LOWER(REPLACE(CONVERT(nvarchar(36), NEWID()), '-', ''));
```

## Authentication

Beacon's sessions use the `Beacon.Auth` cookie (`HttpOnly`, `SameSite=Lax`, `SecurePolicy=SameAsRequest`, sliding; `Beacon:Authentication:Cookie:SecureAlways` set to `true` always issues it with the `Secure` flag; this covers the `Beacon.Auth` cookie only, not the antiforgery or OIDC cookies). There is no default account: [first-run setup](/security/user-management/#first-run-setup) creates the super admin. Register cookie authentication with the UI's base path:

```csharp
builder.Services.AddBeaconCookieAuthentication("/beacon");   // login page /beacon/login
```

### Where Beacon credentials apply

Beacon's API keys, bearer tokens and `Beacon.Auth` cookie authenticate Beacon's own routes, everything under `/beacon`, and the path prefixes listed in `Beacon:Authentication:AdditionalPaths`. On every other route of the host, `ApiKeyAuthMiddleware`, the bearer JWT middleware and `BeaconCookieAuthMiddleware` do nothing, and the login and first-run redirects never send a request to Beacon's pages: the host's own authentication decides.

| Key | Type | Default | What it does |
|---|---|---|---|
| `AdditionalPaths` | string[] | empty | More path prefixes where Beacon credentials apply, for example `/warp` for a Warp dashboard that admits Beacon admins. Matched by whole segments (`/warp` matches `/warp/jobs`, not `/warpx`), ignoring case, against the request path after the host's `PathBase`. Each entry starts with `/` and is not `/` alone, with no query or fragment; a trailing `/` is ignored. An invalid entry fails startup |

```json
{ "Beacon": { "Authentication": { "AdditionalPaths": [ "/warp" ] } } }
```

`app.UseBeaconAuthentication()` wires Beacon's authentication in the order it needs: `ApiKeyAuthMiddleware`, the bearer JWT middleware, the host's `UseAuthentication()`, then `BeaconCookieAuthMiddleware`, followed by the origin check, with the security headers before them all unless the host added them earlier (`Beacon:Security`, below). Call it in place of `UseAuthentication()`, before `UseAuthorization()`.

The cookie scheme (`Cookies`) applies the same scope itself. When it is the host's default authentication scheme (set as the default, or the only scheme registered), or a host policy names it, it authenticates nothing outside `/beacon` and `AdditionalPaths`: the session is refused there without a check against the user store, without renewal, and its cookie is left as it is. A host route that requires an authenticated user therefore needs the host's own scheme.

:::caution[Upgrading]
Beacon credentials no longer authenticate the host's own routes. A host route that relied on a Beacon session, API key or bearer token now sees an anonymous request: add its prefix to `Beacon:Authentication:AdditionalPaths`. A host that serves the Warp dashboard with Beacon's admin check (as the sample does) must list `/warp`, or the dashboard refuses every request. `/openapi/v1.json` and the `/.well-known` discovery documents are host routes and are no longer redirected to Beacon's login or setup page. Hosts that wired the four middlewares one by one keep working unchanged; `UseBeaconAuthentication()` is the shorter form.
:::

### Security headers and the origin check (`Beacon:Security`)

| Key | Type | Default | What it does |
|---|---|---|---|
| `Csp` | `Enforce` \| `ReportOnly` \| `Off` | `Enforce` | The Content-Security-Policy on responses under `/beacon`. The other security headers are sent on all of Beacon's routes in every mode |
| `OriginGuard` | `LogOnly` \| `Enforce` | `LogOnly` | Whether a state-changing, realtime-hub or anonymous Beacon request from a foreign origin is only logged or answered 403 |
| `AllowedOrigins` | string[] | empty | The origins browsers reach Beacon from (`https://beacon.example.com`, with an optional port, no path). With `Beacon:PublicBaseUrl` they replace the default, the request's own origin. An invalid entry fails startup |

`AddBeaconApiServices()` binds the section and `UseBeaconAuthentication()` applies it. See [Security headers and the origin check](/integrate/host-setup/#security-headers-and-the-origin-check).

### Session lifetime

Bound from `Beacon:Authentication:Session`, validated at startup.

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `RevalidateMinutes` | int | `5` | 0–1440 (`0` = every request) | How often, at most, a session is checked against the user store (user exists, enabled, not archived, same security stamp; roles replaced by the stored ones). Also how long a realtime connection lives before it is checked again (at least 1 minute) |
| `AbsoluteLifetimeHours` | int | `12` | 1–8760 | A session ends this long after sign-in, however often it is renewed; caps the cookie's expiry and "remember me" |

### Local password sign-in

Bound from `Beacon:UserManagement:LocalLogin`, validated at startup.

| Key | Type | Default | What it does |
|---|---|---|---|
| `Enabled` | bool | `true` | Password sign-in through the login form. `false`: `POST /beacon/api/auth/login` is not mapped (unless a break-glass account is listed), password providers refuse every account, and the login page shows no password form. Startup fails when nobody could sign in (no OIDC, no `BeaconApiOptions.ExternalLogin` (a host-owned login page), no break-glass account) |
| `BreakGlassUserNames` | string[] | empty | Up to 3 stored user names (exact, case-sensitive) that may still sign in with a password while `Enabled` is `false`, at `/beacon/login?local=1`. A blank entry or more than 3 fail startup; a name no enabled internal user has gets a startup Warning |

See [User management → Local password sign-in](/security/user-management/#local-password-sign-in).

### Optional: OIDC / SSO

Bound by `builder.Services.AddBeaconOidcAuthentication(builder.Configuration)` from `Beacon:Authentication:Oidc`; validated at startup when `Enabled`.

```json
{
  "Beacon": {
    "Authentication": {
      "Oidc": {
        "Enabled": true,
        "Authority": "https://login.microsoftonline.com/{tenant-id}/v2.0",
        "ClientId": "{client-id}",
        "AllowedTenants": [ "{tenant-id}" ],
        "RequiredRoles": [ "Beacon.User" ],
        "DisplayName": "Microsoft"
      }
    }
  }
}
```

| Key | Type | Default | What it does |
|---|---|---|---|
| `Enabled` | bool | `false` | Turns SSO on |
| `Authority`, `ClientId`, `ClientSecret` | string | Required | All three required when enabled. Authorization code flow with PKCE. Supply `ClientSecret` as `Beacon__Authentication__Oidc__ClientSecret` or from a secrets provider |
| `CallbackPath` | string | `/signin-oidc` | The redirect callback |
| `Scopes` | string[] | `openid`, `profile`, `email` | Requested scopes (replaces the list) |
| `AllowedTenants` | string[] | empty | Entra tenant ids (`tid`) that may sign in. Startup fails when empty, unless `AllowAnyTenant` |
| `AllowAnyTenant` | bool | `false` | Skip the tenant check (an identity provider without `tid`). A Warning at startup with an Entra authority |
| `BlockGuests` | bool | `true` | Refuse Entra B2B guests (`acct=1`, or an `idp` naming another provider) |
| `RequiredRoles` / `RequiredGroups` | string[] | empty | When either is set, a user must hold one listed app role (`roles`) or group (`groups`). Prefer app roles: Entra drops `groups` when a user is in too many |
| `DefaultRoleName` | string | unset (no role) | Role for a user provisioned at first sign-in. May not be Admin or a functional role (startup fails) |
| `DisplayName` | string | `SSO` | The SSO button's label |
| `EnableFrontChannelLogout` | bool | `false` | Serve `/signout-oidc`, an anonymous `GET` that ends the session |
| `RequireMfa` | bool | `false` | Refuse an interactive SSO sign-in whose ID token shows no MFA (`amr` contains `mfa`, or `acr` in `AcceptedAcrValues`). Covers the interactive SSO sign-in only |
| `AcceptedAcrValues` | string[] | empty | `acr` values that count as MFA |
| `McpJwksEndpoint` | string (URL) | None | Sample host: with SSO enabled, turns on bearer JWT with issuer = `Authority`, audience = `ClientId` and this JWKS URL. Not read by the Beacon libraries |

Admission runs before anything is provisioned: tenant, then guest, then required role or group. See [User management → Single sign-on admission](/security/user-management/#single-sign-on-admission).

### JWT bearer

Bearer tokens on the REST API and the external login API are configured in code; there is no configuration section:

```csharp
builder.Services.AddBeaconJwtAuthentication(jwt =>
{
    jwt.EnableBearerAuthentication = true;
    jwt.Validation.JwksEndpoint = "https://login.microsoftonline.com/{tenant}/discovery/v2.0/keys";
    jwt.Validation.ValidIssuer = "https://login.microsoftonline.com/{tenant}/v2.0";
    jwt.Validation.ValidAudience = "{beacon-client-id}";
});
```

| Option | Default | What it does |
|---|---|---|
| `ExternalLoginEndpoint` | None | The login API the login form posts to, an `https` URL; internal users sign in first (`HybridAuthenticationProvider`) |
| `EnableBearerAuthentication` | `false` | Accept `Authorization: Bearer <jwt>` on the REST API |
| `AccessTokenClaim` | None | For a non-Entra issuer without `typ: at+jwt`: a claim only its access tokens carry. A claim ID tokens carry stops startup |
| `Validation.SigningKey` / `JwksEndpoint` | None | One is required. A signing key is at least 32 bytes (HS256/384/512 only); the JWKS endpoint is an `https` URL (RS, PS and ES 256/384/512 only). `http` to a loopback host is accepted in Development only |
| `Validation.ValidIssuer(s)`, `ValidAudience(s)` | None | At least one of each is required |
| `Validation.ClockSkew` | 5 minutes | At most 5 minutes |
| `Validation.ValidateLifetime`, `ValidateIssuer`, `ValidateAudience` | `true` | Must stay `true` |
| `ClaimsMapping.*` | `sub`, `preferred_username`, `email`, `roles`, `name` | Which claims hold the user id, user name, e-mail, roles and display name |

With bearer or external login on, a missing or short key, a missing issuer or audience, a validation flag turned off, a skew above 5 minutes or an `http` endpoint stops the host. A token must be an access token and name an existing, enabled, external Beacon user; see [User management](/security/user-management/#jwtexternalapiauthenticationprovider). Entra callers on `/beacon/mcp` are configured separately (below).

### MCP callers from Entra ID (`Beacon:Mcp:Callers`)

Validated at startup. Without the section, no JWT caller is admitted on `/beacon/mcp`. Active callers also need bearer JWT enabled with issuer and audience validation.

| Key | Type | Default | What it does |
|---|---|---|---|
| `AllowedTenants` | string[] | empty | Tenants whose tokens map to a caller; required when callers are active |
| `Users:Enabled` | bool | `false` | Accept delegated user tokens |
| `Users:RequiredRoles` / `RequiredGroups` | string[] | empty | Any-of admission rules; both empty fails startup unless `AllowAnyTenantUser` |
| `Users:AllowAnyTenantUser` | bool | `false` | Admit every user of the allowed tenants |
| `Users:ProjectIds` | int[] | empty | Projects for every accepted user |
| `Users:GroupProjects` | map group or role → int[] | empty | Extra projects per group or app role |
| `Users:Scope` | `Read` \| `Execute` | `Execute` | Scope of user callers |
| `Users:AutoProvision` | bool | `true` | Find or create a Beacon user keyed on `oid` |
| `Users:DefaultRoleName` | string | `Viewer` | Role for provisioned users; not Admin or a functional role |
| `Systems[i]:Name` | string | Required | Required, unique |
| `Systems[i]:ClientId` or `ObjectId` | string | Required | Exactly one: an app-only token's client, or a service user's `oid` |
| `Systems[i]:ProjectIds`, `Scope`, `HostClaims[]` | | `Execute` | The system caller's projects, scope and host-endpoint claims |

See [Entra ID callers](/mcp/entra-callers/).

## Data-source transport security

Every data-source connection must authenticate the server over TLS (for example `SSL Mode=VerifyFull` on PostgreSQL,
`SslMode=VerifyFull` on MySQL, `Encrypt=Mandatory` or `Strict` without `TrustServerCertificate=true` on SQL Server and
Azure Synapse). The rule per engine is in [Transport security (TLS)](/features/data-sources/#transport-security-tls).
Creating a data source, changing its connection and testing a connection refuse a connection that does not meet it.
Bound from `Beacon:DataSources:Transport`:

| Key | Type | Default | What it does |
|---|---|---|---|
| `EnforceOnExisting` | bool | `false` | Refuses a stored data source whose connection does not meet the rule every time it connects. While `false`, such a source keeps working, the data-source list reports it (`transportVerified: false`) and the log carries one Warning per data source per process |
| `AllowInsecure` | bool | `false` | Lifts the rule for new, changed and stored connections alike. For development hosts whose local databases have no TLS; outside Development the host logs a Warning at startup while it is set. The sample host sets it in `appsettings.Development.json` only |

```json
{ "Beacon": { "DataSources": { "Transport": { "EnforceOnExisting": true } } } }
```

Host-managed data sources follow the same rules, applied to the `ConnectionStrings:{Name}` value each time they connect.
That value is checked as a stored connection, never refused as a new one: a value that does not meet the rule is logged
as a Warning, and refused only while `EnforceOnExisting` is `true`.

:::caution[Upgrading]
PostgreSQL and MySQL data sources stored without `VerifyFull`, and SQL Server or Azure Synapse sources with
`Encrypt=False` or `TrustServerCertificate=True`, keep working but are flagged and logged once per process; new ones are
refused. Replace each flagged connection, then set `EnforceOnExisting` to `true`. A development host that connects to
local databases without TLS sets `AllowInsecure` in its Development configuration only.
:::

## Authorization

Bound from `Beacon:Authorization`, validated at startup. Whether authorization is on at all is code-only (`options.Authorization.Enabled`). See [Authorization](/security/authorization/).

| Key | Type | Default | What it does |
|---|---|---|---|
| `RequireProjectMembership` | bool | `false` | Restrict every project to its members and Admins; data sources no project links become Admin-only. Startup fails without a membership source (Beacon user management or a host `IProjectMembershipSource`) |

### Functional permissions: role names, identity-provider app roles, separation of duties

```json
{
  "Beacon": {
    "Authorization": {
      "Roles": {
        "Auditor": "Compliance Reviewer",
        "HonourHostRoleClaims": false
      },
      "IdpRoles": {
        "Enabled": true,
        "Map": { "beacon.auditor": "Auditor" },
        "ClaimLifetimeMinutes": 60
      },
      "SeparationOfDuties": { "Enabled": true }
    }
  }
}
```

| Key | Type | Default | What it does |
|---|---|---|---|
| `Roles:Approver`, `Auditor`, `SecurityAdmin`, `UserAdmin`, `DataSourceAdmin`, `MigrationOperator` | string | the permission's name | The level-0 system role that grants the permission. At most 50 characters, not `Admin`, `Editor` or `Viewer`, unique across permissions (ignoring case); blank keeps the default |
| `Roles:HonourHostRoleClaims` | bool | `false` | Host **without** Beacon user management: let session role claims named like a permission's role grant it. No effect with user management |
| `IdpRoles:Enabled` | bool | `false` | Map identity-provider app roles to permissions at SSO sign-in (session claims only, never stored). Needs at least one `Map` entry |
| `IdpRoles:Map` | map app role → permission | empty | Only the six permissions; never Admin, Editor or Viewer |
| `IdpRoles:ClaimLifetimeMinutes` | int | `60` | 1–1440. How long a mapped permission counts after the sign-in |
| `SeparationOfDuties:Enabled` | bool | `false` | Refuse (`400`) assignments giving one user Approver + SecurityAdmin, Auditor + SecurityAdmin, Auditor + UserAdmin, UserAdmin + Approver or UserAdmin + MigrationOperator; an `IdpRoles:Map` onto such a pair fails startup |

## User management and first-run setup

User management itself is code-only ([Code-only options](#code-only-options)); these keys are bound from configuration:

| Key | Type | Default | What it does |
|---|---|---|---|
| `Beacon:UserManagement:SetupToken` | string | unset | The secret first-run setup asks for. At least 32 characters after trimming, or startup fails (checked when user management is on). Unset: Beacon generates a token per process and prints it once to the process console (standard error, never the log) while no super admin exists. Set it when several replicas serve the setup page |
| `Beacon:UserManagement:LocalLogin:*` | | | See [Local password sign-in](#local-password-sign-in) |

Login attempts are throttled at 10 per 60 seconds per client address; the first-run setup endpoints have their own budget. Neither is configurable.

## API keys

Bound from `Beacon:ApiKeys`, validated at startup. See [API keys](/security/api-keys/).

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `MaxLifetimeDays` | int | `365` | 1–3650 | Longest lifetime a new key may have; a key created without a date gets 90 days, capped at this |
| `EnforceMaxLifetimeOnExistingKeys` | bool | `false` | | A key stored without an expiry expires `MaxLifetimeDays` after creation. Off: such keys keep working and warn once per process |
| `MaxOwnerInactivityDays` | int | `0` (off) | 0–3650 | A key stops working (`401`) while its owner has not signed in with their own identity for this many days |

Scopes are `Read` and `Execute`; a key stored with the retired `Admin` scope is read as `Execute`.

## Query execution row cap

Bound from `Beacon:Query`, validated at startup. These apply to every statement Beacon runs for its own features: previews, subscription runs, AI actor checks, ad-hoc queries.

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `MaxExecutionRows` | int | `100000` | 1–10,000,000 | The most rows one statement may return. The cap is written into the SQL (a larger `LIMIT`/`TOP`/`FETCH` is lowered to it); on PostgreSQL, MySQL, SQL Server, Azure Synapse and Snowflake the read also stops one row past it, BigQuery stops reading the job's result there, and Databricks returns no more than that (`row_limit`). A larger result **fails** the run, never truncates, with a message naming the key. Rows are held in memory |
| `StatementTimeoutSeconds` | int | `120` | 1–3600 | Cancels one statement: a query step, a final query on the in-memory join, a metadata sample. A subscription's own timeout may only be shorter (saving a longer one is refused). On Snowflake the session also runs under `STATEMENT_TIMEOUT_IN_SECONDS` of this value, so the server stops the statement too |
| `MaxResultBytes` | long | `268435456` (256 MiB) | greater than 0 | The most bytes one statement's result may hold while it is read from a PostgreSQL, MySQL, SQL Server, Azure Synapse or Snowflake data source, capped or not, and while a final query's result is read from the in-memory join. Estimated as the rows arrive: each row 32 bytes plus 8 a column, each value at least 8 bytes (text two bytes a character, binary its length, a number, date, time or identifier 16, an array, list, record or map its elements however deeply nested, any other value two bytes a character of its text form). A larger result **fails** the run as soon as it passes the limit, with "The result is larger than the N-byte limit (Beacon:Query:MaxResultBytes); select fewer columns or rows."; no value or row is cut short. One row (on PostgreSQL, one value) is read whole before it is counted, and the rows are copied on their way to the caller, so peak memory can be a few times the limit. Not the MCP **Max result size** (`maxResultBytes` in the MCP settings), which bounds the result an MCP tool returns to its client — see [MCP server](/mcp/server/#configuration) |

Opening a connection to a PostgreSQL, MySQL, SQL Server or Azure Synapse data source waits at most 30 seconds: a connection string that sets no connect timeout (`Timeout`, `Connection Timeout`, `Connect Timeout`), or `0`, gets 15 seconds, and a longer one is lowered to 30. Snowflake keeps its driver's own login timeout.

Data migration reads use `Beacon:Migrations:MaxRows` instead. The MCP tools that run SQL use the project's MCP statement timeout (30 seconds by default).

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `Beacon:Metadata:ForcedRefreshCooldownMinutes` | int | `15` | 0–1440 (`0` = off) | After a successful forced metadata refresh of a data source, the next one answers `429` until this passes. Per process; Admins and DataSourceAdmins are not held to it. Validated at startup |
| `Beacon:AiActors:ThinkCooldownMinutes` | int | `15` | 0–1440 (`0` = off) | An AI actor's think cycle starts at most once per this many minutes, from its last think time. Background cycles within it are skipped; an on-demand cycle is refused with the time left. Validated at startup |
| `Beacon:AiActors:PlanExpiryDays` | int | `7` | 1–365 | An AI actor's plan awaiting approval expires this many days after it was proposed: it is no longer listed as pending, and approving, rejecting or revising it answers `409`. Computed when read. Validated at startup |

## Request limits

Bound from `Beacon:RateLimits`, validated at startup. Each limit is a fixed window: `PermitLimit` requests per `WindowSeconds` (1–3600, default `60`) in each partition; `PermitLimit` is 0–100,000 and `0` turns that limit off. An authenticated caller is one partition per credential (an API key by its id, a mapped MCP caller by its subject hash, a signed-in user), an anonymous one per client address: an IPv4 address (also one mapped into IPv6) whole, an IPv6 address by its `/64` block, so one client cannot spread its requests over the addresses of its block. Each limit keeps at most `Beacon:RateLimits:MaxPartitions` partitions (default `10000`, 100–1,000,000); a new one beyond that replaces the one used least recently, which starts over with a full budget. The address is `HttpContext.Connection.RemoteIpAddress`, which `UseForwardedHeaders` rewrites only as your host configures it: behind a reverse proxy without forwarded headers every caller shares the proxy's partition (see [Production checklist](/integrate/production/#reverse-proxy)). Beacon's endpoints apply the limits themselves, on Beacon's routes only; `AddRateLimiter`/`UseRateLimiter` are not needed and a host's own limiter does not change them. The counts are kept in memory, so with N instances behind a load balancer a caller can reach N times a limit. Over a limit the answer is `429` as `application/problem+json` with `Retry-After` (whole seconds).

| Key | Default | Partition | What it limits |
|---|---|---|---|
| `Login` | `10` a minute | client address | Password sign-in (`POST /beacon/api/auth/login`), and first-run setup in its own partition |
| `Api` | `600` a minute | caller (anonymous: address) | Every `/beacon/api` request |
| `Anonymous` | `30` a minute | client address | Anonymous requests to Beacon's routes answered `401` (no credential, an unknown or expired API key or bearer token); over it they get `429` instead |
| `Mcp` | `60` a minute | API key or MCP caller | Every request to `/beacon/mcp` |
| `Ask` | `10` a minute | caller | The `ask` tool, through MCP and the playground; over it the tool answers an error (audited like every refusal) |

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `Beacon:RateLimits:Lockout:Enabled` | bool | `true` | | Apply the lockout. `false` (or `MaxFailedAttempts` `0`) turns it off: password sign-in is then limited only per client address by `Login` |
| `Beacon:RateLimits:Lockout:MaxFailedAttempts` | int | `5` | 0–100 (`0` = off) | After this many wrong passwords an internal user's password sign-in, and the password change of a signed-in user, is locked. Counted per user (stored on the user, so it holds across instances), a wrong current password on a password change included; a name no user has is counted against nobody. The break-glass accounts (`Beacon:UserManagement:LocalLogin:BreakGlassUserNames`) are never counted or locked: they stay limited per client address by `Login` |
| `Beacon:RateLimits:Lockout:DurationMinutes` | int | `15` | 1–1440 | How long the lock lasts. While locked every password, the right one included, gets the same generic answer as a wrong one, after the same password check; wrong passwords while locked do not extend it. The lock is decided after the password check, together with the write that clears the count, so attempts already in flight when another attempt locks the user are refused too. A successful sign-in, the lock itself, an administrator's password reset and a wrong password sent this long or longer after the previous one start the count again. A lock records `auth.user_locked_out` and logs `UserLockedOut` (7124); a refused attempt records `auth.password_sign_in_refused` with reason `locked_out` |

The lockout counts per user, not per client: anyone who knows a user name can lock that user's password sign-in by sending wrong passwords, and again each time the lock ends (the per-address `Login` limit slows this down, but several addresses get past it). That denial of service is the price of a hard cap on password guessing. Keep a break-glass account for emergencies (it is never locked), prefer single sign-on for everyday users, or set `Lockout:Enabled` to `false` when the denial of service weighs more for your deployment than the cap.

## Request size and time limits

Bound from `Beacon:RequestLimits`, validated at startup. They apply to Beacon's own routes only (`/beacon/api`, the hub under it and `/beacon/mcp`), through `UseBeaconAuthentication()`, and a host's own limit wins where it is stricter: a lower Kestrel `MaxRequestBodySize` is never raised. `0` turns one bound off. They do not touch the 5-minute Kestrel keep-alive and header timeouts or the 5-minute `HttpClient` timeout that AI calls need (see [Host setup](/integrate/host-setup/)).

| Key | Type | Default | What it does |
|---|---|---|---|
| `ApiMaxBodyBytes` | long | `2097152` | Largest request body on `/beacon/api`; a larger one answers `413` |
| `McpMaxBodyBytes` | long | `4194304` | Largest request body on `/beacon/mcp` |
| `ApiTimeoutSeconds` | int | `300` | A `/beacon/api` request still running after this has its cancellation token cancelled and, if nothing was sent yet, answers `504`. The hub and the routes that run long work inline (migration run, metadata refresh, documentation generation, repository scan, eval run, playground tool run) are exempt and keep their own timeouts. A host's own long route opts out with `.WithBeaconLongRunning()` |
| `HubMaxReceiveMessageBytes` | long | `8192` | Largest message the hub takes from a client (it has no client-callable methods) |
| `HubMaxParallelInvocationsPerClient` | int | `1` | Hub invocations in flight per connection |
| `HubClientTimeoutSeconds` | int | `30` | Silence after which the hub drops a connection |
| `McpMaxIdleSessions` | int | `1000` | Idle MCP sessions kept in memory (`HttpServerTransportOptions.MaxIdleSessionCount`; the SDK default is 10,000). A session is dropped after 30 idle minutes |
| `McpMaxInitializesPerCaller` | int | `60` | MCP `initialize` requests one caller (API key, mapped caller, user or address) may send per window; more answer `429` with `Retry-After`. Per instance, in memory |
| `McpInitializeWindowSeconds` | int | `60` | The window of the previous key |

A host that configures SignalR (`BeaconApiOptions.ConfigureSignalR`, `AddHubOptions`) or `HttpServerTransportOptions` itself overrides the hub and idle-session values.

## Subscription limits

Bound from `Beacon:Subscriptions`, validated at startup.

| Key | Type | Default | Range | What it does |
|---|---|---|---|---|
| `MinIntervalMinutes` | int | `5` | 1–1440 | The shortest time between two runs of a subscription's cron schedule (in UTC). Creating a subscription with a schedule that fires more often (an AI actor's included), or changing one to it, is refused; a subscription saved before keeps its schedule through other edits and a reactivation |
| `MaxAttachmentBytes` | int | `10485760` (10 MB) | 65,536–104,857,600 | The largest file a subscription attaches to its email. A larger CSV or XLSX keeps only its first rows that fit (a CSV exactly as many; an XLSX file can keep somewhat fewer), and the email says how many of the rows it delivers the file holds; a first row larger than the limit leaves the column headers only, and headers larger than the limit leave no attachment. The email itself is about 1.33 times the attachment's size (base64 encoding), so keep the limit below your mail server's message limit |

See [Subscriptions](/features/subscriptions/).

## Data migration

Bound from `Beacon:Migrations`, validated at startup. **A deployment lock**: only configuration turns it on.

| Key | Type | Default | What it does |
|---|---|---|---|
| `Enabled` | bool | `false` | Maps the migration endpoints, shows the feature, lets jobs run and lets an Admin mark data sources as migration write targets. Off: nothing runs, for Admins too |
| `MaxRows` | int | `100000` | 1–10,000,000. One run's read and write budget; a larger source fails before anything is written |

See [Data migration](/features/data-migration/).

## Approval workflow

The four-eyes workflow is **off** unless the host turns it on, in code. See [Approvals](/security/approvals/) for what it governs.

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    // ...
    options.EnableApprovalWorkflow(x =>
    {
        x.RequireApprovedVersionToRun = true; // strict mode
    });
});
```

| Option (`options.ApprovalWorkflow.*`) | Default | What it does |
|---|---|---|
| `Enabled` | `false` | Every change to a query's executable content waits for a second person's approval; subscriptions run only reviewed SQL |
| `AllowSelfApproval` | `false` | Lets the requester approve their own change (for a host with one approver) |
| `RequireApprovedVersionToRun` | `false` | Strict mode: a subscription or saved-query MCP tool runs only a version approved for exactly its content |
| `CoversMigrationJobs` | `true` | While the workflow is on, a new migration job runs only after approval |
| `CoversCustomSqlRules` | `true` | While the workflow is on, a new or changed Custom SQL rule is evaluated only after approval |
| `CoversRecipientDestinations` | `true` | While the workflow is on, a new recipient or changed destination receives nothing until approved |
| `CoversDataSourceConnections` | `false` | While the workflow is on, a new data source or a connection changed in anything but its secrets connects to nothing until approved |
| `CoversLlmConnection` | `false` | While the workflow is on, a change of the stored LLM connection's provider, endpoint, region or authentication mode waits for a live Admin's approval; the stored connection stays in use meanwhile |
| `LlmConnectionRequestDays` | `7` | How long a staged LLM connection change waits for a decision before it expires and what it staged is wiped; held between 1 and 30 |

The `Covers…` switches apply only while `Enabled` is on. Turning a switch, or the workflow, off releases what it held; startup warns with the counts. Startup also logs how changes are governed (a Warning outside Development while the workflow is off).

## Security audit trail

Beacon records sign-ins and changes to identities, keys, access, data sources, delivery, settings, approvals, ownership, tasks, curation, data quality and migrations in an append-only, signed trail an Auditor reads (see [Security audit trail](/security/audit-trail/)). It needs no configuration; these settings tune it:

```json
{
  "Beacon": {
    "Audit": {
      "IntegrityKey": "<from a secret store>",
      "PreviousIntegrityKeys": [],
      "RetentionDays": null
    }
  }
}
```

| Key | Type | Default | What it does |
|---|---|---|---|
| `Beacon:Audit:IntegrityKey` | string | derived | The secret events are signed with: at least 32 characters, supplied like the encryption key (a secret store, or `Beacon__Audit__IntegrityKey`), never committed. Unset, Beacon derives a key for this purpose from `Beacon:Mcp:CallerHashKey`, else from `Beacon:EncryptionKey`. Set it before you change the encryption key |
| `Beacon:Audit:PreviousIntegrityKeys` | string[] | empty | Integrity keys used earlier, as a list (`Beacon__Audit__PreviousIntegrityKeys__0`, ...): accepted when verifying events, never used to sign. Move the old key here when you set a new one |
| `Beacon:Audit:RetentionDays` | int | none (keep forever) | Days events are kept: between `365` and `36500`. The purge must be scheduled; the sample host runs it daily as the Warp job `security-audit-retention` (see [Retention](/security/audit-trail/#retention)) |

An integrity key shorter than 32 characters or containing `${`, or a retention outside the range, stops the host at startup with a message that never contains a key. At startup the host logs the id of the key events are sealed under and where it comes from (never the key), with a Warning while `IntegrityKey` is unset.

Two jobs keep the trail checked and bounded; neither runs unless the host schedules it. The sample host schedules both as Warp recurring jobs: `security-audit-verify` (hourly at `:15`), which verifies the events written since its last run and writes a checkpoint to the `Beacon.Audit` log stream, and `security-audit-retention` (daily), the purge. A host embedding Beacon schedules `ISecurityAuditVerificationService.VerifyNewEventsAsync` and `ISecurityAuditRetentionService.PurgeExpiredAsync` itself (see [Scheduled verification](/security/audit-trail/#scheduled-verification)). Alert on the counters `beacon.security_audit.write_failures` and `beacon.security_audit.verification_failures` above zero.

## AI / LLM

Bound from `Beacon:LLM` by `builder.Services.AddBeaconAI(builder.Configuration)`. Optional; not validated. AI features also need `options.UseAI = true`.

| Key | Type | Default | What it does |
|---|---|---|---|
| `Provider` | `OpenAI` \| `Claude` \| `AzureOpenAI` \| `Bedrock` | `OpenAI` | The provider (`Claude` is Anthropic). `Bedrock` needs `Moberg.Beacon.AI.Bedrock` + `AddBeaconBedrock()` |
| `ApiKey` | string | `""` | The provider key. Bedrock: a legacy `accessKey:secretKey` pair, used when `AwsAccessKeyId` and `AwsSecretAccessKey` are both empty (under `IamRole` with a logged warning) |
| `BedrockAuthMode` | `IamRole` \| `AccessKey` \| `TemporaryCredentials` | `IamRole` | Bedrock credentials: the default AWS credential chain, the access key pair, or the pair with `SessionToken` |
| `AwsAccessKeyId`, `AwsSecretAccessKey` | string | None | The Bedrock access key pair for `AccessKey` and `TemporaryCredentials`; one without the other fails |
| `Endpoint` | string | None | Required for `AzureOpenAI` |
| `Region` | string | None | Required for `Bedrock` |
| `SessionToken` | string | None | Bedrock temporary credentials |
| `Model` | string | `""` | The model; for Azure the deployment name, for Bedrock the model id |
| `FastModel` | string | None | Model for the short classification calls on the `ask` path (intent classification and data-source routing). Empty or equal to `Model`: those calls use `Model`, as does every other call. If the fast model fails when called, the call is served once on `Model` |
| `Limits:MaxConcurrentRequests` | int | `50` | Concurrency of the LLM request queue; a value saved in Admin Settings replaces it without a restart. Background work (documentation generation, indexing, learning, scheduled actor cycles, eval judging) holds at most half the slots (at least one) |
| `Limits:InteractiveQueueWaitSeconds` | int | `60` | How long an interactive call waits for a free slot before it fails with *The LLM is busy. Try again later.* (`LlmQueueSaturatedException`). Non-positive: the default; at most `3600` |
| `Limits:BackgroundQueueWaitSeconds` | int | `300` | The same wait for background work. Non-positive: the default; at most `3600` |
| `Limits:MaxQueuedRequests` | int | `200` | How many interactive calls may wait for a slot at once; background work may have half as many waiting (at least one). One more fails as busy at once. Non-positive: the default |
| `Limits:RateLimitsEnabled` | bool | `false` | Enforce `TokensPerMinute` and `RequestsPerMinute` (a per-minute token bucket each; a call that cannot get its share within its queue wait fails as busy). Admin Settings' **Enforce requests/tokens per minute** replaces it once saved; like the other limits saved there, the stored value is applied again at startup |
| `Limits:TokensPerMinute`, `RequestsPerMinute` | int, int | `80000`, `1000` | Applied only while `RateLimitsEnabled` is on; `0` or less is unlimited. A call reserves its prompt characters / 4 plus its maximum answer tokens and settles them with the reported usage |
| `Limits:MonthlyBudget` | decimal | `100.00` | Recorded, not enforced (Beacon keeps no record of spend) |

```json
{
  "Beacon": {
    "LLM": {
      "Provider": "Claude",
      "Model": "<model-id>",
      "FastModel": "<fast-model-id>"
    }
  }
}
```

```bash
export Beacon__LLM__ApiKey="<provider key>"
```

At startup the provider is built from `Beacon:LLM`. Saving the AI provider in [Admin settings](/security/admin-settings/) swaps it immediately, without a restart; after a restart `Beacon:LLM` applies again until the settings are saved. Keep `Beacon:LLM` in step with Admin settings, or save them again after a restart. See [AI providers](/integrate/ai/).

### Local embeddings (`Beacon:Embeddings`)

| Key | Type | Default | What it does |
|---|---|---|---|
| `Enabled` | bool | `false` | Run a local ONNX embedding model in-process for semantic retrieval |
| `ModelPath` | string | None | The ONNX model file (384 dimensions, for example bge-small-en-v1.5) |
| `TokenizerPath` | string | None | The WordPiece vocabulary file |

These keys are read by the `Moberg.Beacon.AI.Embeddings.Onnx` package (`AddBeaconOnnxEmbeddings()`). The service is available only when `Enabled` is `true` and both files exist; otherwise retrieval falls back to lexical matching. On PostgreSQL the vectors live in a pgvector column, so the `vector` extension must be available to Beacon's migrations; on SQL Server they are compared in memory. The sample host re-indexes every 12 hours (`mcp-embedding-reindex`, `mcp-docchunk-reindex`).

## GitHub repository scan (`Beacon:GitHub`)

A project's repository scan reads source files from `https://github.com` with the repository's stored token. Only `https://github.com/owner/repo` URLs (or `owner/repo`) are scanned; a URL on another Git host (GitHub Enterprise Server, GitLab, Azure DevOps) fails the scan with a message saying so, and no request is made. Every scan is bounded:

| Key | Type | Default | Meaning |
|---|---|---|---|
| `MaxFilesPerRepository` | int | `5000` | The most source files read from one repository. The rest are left out |
| `ScanTimeout` | timespan | `00:05:00` | The longest one repository scan runs. A scan that reaches it keeps what it found |
| `MaxFileBytes` | int | `1000000` | A larger source file is skipped |
| `MaxTreeResponseBytes` | int | `33554432` | The most bytes read from the repository's file list |

A scan that hits a bound still ends as completed, and its error text starts with "Partial scan:" and names what was left out (files over the cap, files over the size limit, the time limit, files that could not be read, or a file list GitHub itself truncated), so a partial result is never mistaken for a full one. The project's repository list marks such a repository **Partial** with the reasons (`partialScan` and `partialScanReasons` on each repository of `GET /beacon/api/projects/{id}`), and `POST /beacon/api/projects/{id}/scan` returns them in `partialScans`. The time limit also covers reading the repository's metadata and file list: a scan that reaches it before the file list is read fails. A scan replaces the repository's code references only when it finishes; one that fails or is cancelled by the caller keeps the previous references.

## Notification destinations (optional)

Bound from `Beacon:Notifications`, validated at startup (each bad entry is named); the effective policy is logged once.

| Key | Default | What it does |
|---|---|---|
| `AllowedHosts:Slack` / `Teams` / `Jira` | empty | Extra hosts on top of the built-in ones: Slack `hooks.slack.com`; Teams `*.webhook.office.com`, `*.logic.azure.com`, `*.environment.api.powerplatform.com`; Jira `*.atlassian.net`, `api.atlassian.com`. `*.example.com` matches subdomains only; `*.com` is refused |
| `AllowedHosts:Webhook` | empty (any public host) | When set, the only hosts a webhook may target |
| `AllowedHosts:Email` | empty (any domain) | When set, the only e-mail domains |
| `DisabledTypes` | empty | Types this host does not deliver: `Email`, `Teams`, `Slack`, `Jira`, `Webhook` |
| `AllowedPrivateNetworks:Webhook` / `Slack` / `Teams` / `Jira` | empty | IP addresses, CIDR ranges or host names that calls of that type may still reach though private |
| `AllowedHeaders` | empty | Webhook header names allowed besides `Authorization`, `Api-Key`, `X-Api-Key`, `Ocp-Apim-Subscription-Key`. Reserved names (`Host`, `Content-Length`, `X-Forwarded-*`, `Metadata-Flavor`, …) are refused |
| `Nat64Prefixes` | empty | Extra NAT64 `/96` prefixes besides `64:ff9b::/96` |
| `UseSystemProxy` | `false` | Send notification calls through the system proxy, with the destination checked before sending. Requires `AllowedHosts:Webhook` (or Webhook disabled) |
| `RequireEncryptedSecrets` | `false` | Refuse to send to a recipient whose secrets are still stored unencrypted |

Slack, Teams, Jira and webhook destinations need `https` without user credentials. Calls never follow redirects, time out after 30 seconds (10 seconds to connect), and by default connect directly, only to public addresses Beacon resolved and checked itself.

:::caution[Proxy mode]
Through a proxy, the proxy resolves the host name and connects to the address it resolves. Configure the proxy's egress policy to refuse internal, link-local and cloud-metadata destinations, and keep `AllowedHosts:Webhook` short.
:::

Recipients saved by an older version keep working with unencrypted secrets until saved again or until `IRecipientSecretEncryptionService.EncryptStoredSecretsAsync` runs (the sample's `EncryptRecipientSecretsJob`, which the host enqueues once every node runs the new version; an earlier version cannot read the encrypted secrets). Then set `RequireEncryptedSecrets` to `true`. Notification URLs are secrets: leave notification calls out of HTTP tracing (`NotificationRequests.IsNotificationRequest`). See [Notifications](/features/notifications/).

## Email adapter

Beacon's libraries ship no email implementation. Register your own `IEmailAdapter` with `options.AddEmailAdapter<T>()`. The sample host's `BeaconMailSender` reads these keys, at send time:

| Key | Default | What it does |
|---|---|---|
| `Beacon:Smtp:Host` | **required** | SMTP server |
| `Beacon:Smtp:Port` | `465` | Port |
| `Beacon:Smtp:EnableSsl` | `true` | TLS |
| `Beacon:Smtp:Username` | **required** | Login |
| `Beacon:Smtp:Password` | **required** | Password; supply as a secret |
| `Beacon:Smtp:FromAddress` | **required** | Sender address |

A missing required key fails the send, not the startup. These keys belong to the sample's adapter; a host with its own adapter reads whatever it needs.

## Deployment locks

Settings that bind Admins too and that nothing at runtime can change; only host configuration sets them:

| Key | Default | Pins |
|---|---|---|
| `Beacon:Mcp:ForceReadOnly` | **`true`** | MCP read-only enforcement on, globally and per project; the SQL gate judges every statement read-only whatever it is asked. `false` logs a startup Warning |
| `Beacon:Mcp:ForcePiiDetection` | **`true`** | PII detection on. `false` logs a startup Warning |
| `Beacon:Mcp:ForceNoContentRetention` | `false` | Content retention off: questions, SQL and free-text errors are never persisted |
| `Beacon:Mcp:Audit:RequireContent` | `false` | Content retention on, globally and per project, so the MCP audit keeps the question and the SQL of every statement a call ran. Cannot be combined with `ForceNoContentRetention`: with both on the host fails to start. See [Keeping audit content](/mcp/observability-and-audit/#keeping-audit-content-the-requirecontent-lock) |
| `Beacon:Mcp:ForbidRowDataToLlm` | `false` | Sample-value collection, value grounding and the eval judge off: no value read from a data source goes to the LLM provider |
| `Beacon:Mcp:Ceilings:*` | none | Cap the MCP row limit and the SQL and question lengths; the other ceilings clamp recorded settings (see [MCP](#mcp-beaconmcp)) |
| `Beacon:Mcp:Audit:Required` | `false` | Withhold an MCP tool result whose audit row could not be written |
| `Beacon:Migrations:Enabled` | `false` | Data migration, the only write path |

A write that contradicts an MCP lock answers `409` (`/errors/setting-locked`), an Admin's included; the MCP settings page shows the locked settings disabled, and `GET /beacon/api/mcp/settings/locks` lists them (`lockedFields`) and each active lock with the setting it pins (`locks`: `fieldName`, `lockName`). Stored values a lock overrides are kept and listed in one startup warning; they apply again when the lock is lifted. Settings resolve as **lock → project override → global value → built-in default**, ceilings last.

Other startup-validated keys (`Beacon:Authorization:*`, `Beacon:Authentication:*`, `Beacon:UserManagement:*`, `Beacon:ApiKeys:*`, `Beacon:Query:*`, `Beacon:Subscriptions:*`, `Beacon:Notifications:*`, `Beacon:EncryptionKey`, `Beacon:DataProtection:*`) have no runtime override either. Of the configuration values, only the base URL and the LLM settings can also be changed at runtime, in [Admin settings](/security/admin-settings/).

## MCP (`Beacon:Mcp`)

Validated at startup. Without the section, `ForceReadOnly` and `ForcePiiDetection` are on, nothing else is locked, and only the built-in length caps apply.

```json
{
  "Beacon": {
    "Mcp": {
      "ForceReadOnly": true,
      "ForcePiiDetection": true,
      "ForceNoContentRetention": false,
      "ForbidRowDataToLlm": false,
      "Ceilings": { "MaxRowLimit": 1000, "MaxSqlChars": 100000, "MaxQuestionChars": 4000, "AskTimeoutSeconds": 120, "AskMaxLlmCalls": 12, "AskSchemaContextChars": 120000 },
      "Audit": { "Required": true, "RetentionDays": 400, "RequestIdHeader": "X-Request-Id", "RequireContent": false }
    }
  }
}
```

| Key | Type | Default | What it does |
|---|---|---|---|
| `ForceReadOnly`, `ForcePiiDetection`, `ForceNoContentRetention`, `ForbidRowDataToLlm` | bool | `true`, `true`, `false`, `false` | See [Deployment locks](#deployment-locks) |
| `Ceilings:MaxRowLimit` | int | none | Caps the row limit the MCP tools apply; must be greater than 0 when set |
| `Ceilings:StatementTimeoutSeconds`, `MaxResultBytes`, `MaxConcurrentQueriesPerKey` | int | none | Clamp the MCP settings of the same names, shown on the MCP settings page; must be greater than 0 when set. The MCP tools apply the statement timeout to every statement, bound each tool result to the byte budget, and refuse a call when the caller already runs that many queries |
| `Ceilings:MaxExplainCost` | decimal | none | Clamps the MCP `MaxExplainCost` setting, shown on the MCP settings page; greater than 0 when set. The setting is enforced on PostgreSQL data sources: a statement whose planner-estimated total cost (`EXPLAIN`, never run) exceeds it is refused before it runs. Other engines ignore it |
| `Ceilings:MaxSqlChars` | int | `100000` (always applies) | Longest SQL text the gate evaluates |
| `Ceilings:MaxQuestionChars` | int | `4000` (always applies) | Longest question `ask` accepts |
| `Ceilings:AskTimeoutSeconds` | int | `120` (always applies) | Wall-clock time one `ask` call may take, its statements included; past it the call returns an error; 1 to 3600 when set |
| `Ceilings:AskMaxLlmCalls` | int | `12` (always applies) | Logical LLM calls one `ask` call may make (a provider's transport retries are not counted); fewer self-consistency candidates and skipped repairs keep it within them, and a call with none left before it generated SQL returns an error; at least 2 when set (the intent classification and the first SQL generation) |
| `Ceilings:AskSchemaContextChars` | int | `120000` (always applies) | Longest schema context one `ask` sends to the LLM, in characters (about 30,000 tokens at the default); over it whole table descriptions are left out, least relevant to the question first, and one line inside the cap names them. When the grounding sections leave no room for the most relevant table they are left out first (learned patterns, glossary, verified examples, value matches, join paths), and that table is sent even if it alone is longer than the cap. At least 10000 when set |
| `Audit:Required` | bool | `false` | Fail closed: withhold a tool result whose audit row could not be written |
| `Audit:RetentionDays` | int | none (keep forever) | Days audit rows are kept; greater than 0 when set. The sample host purges daily (`PurgeExpiredMcpAuditLogsJob`); other hosts schedule it themselves |
| `Audit:RequestIdHeader` | string | `X-Request-Id` | Upstream correlation header stored with each audit row; empty reads none |
| `Audit:RequireContent` | bool | `false` | See [Deployment locks](#deployment-locks) |
| `CallerHashKey` | string | derived from `Beacon:EncryptionKey` | See [Encryption key](#encryption-key-configuration-required) |

The MCP settings page's own built-in defaults (row limit 1000, PII detection on, content retention on, sample values and value grounding off) are runtime settings, not configuration. See [MCP server](/mcp/server/) and [MCP observability and audit](/mcp/observability-and-audit/).

### MCP discovery (`Beacon:Mcp:ProtectedResource`)

Read at request time.

| Key | Type | Default | What it does |
|---|---|---|---|
| `AuthorizationServers` | string[] | empty | Issuers published in `/.well-known/oauth-protected-resource`; each must equal its server metadata's `issuer` |
| `DeriveAuthorizationServerFromJwtIssuer` | bool | `false` | When `AuthorizationServers` is empty, publish the bearer JWT issuer |
| `ScopesSupported` | string[] | empty | Scopes published for an external identity provider (for example `api://{id}/mcp.access`) |
| `ResourceName` | string | empty | The `resource_name` MCP clients show, also the server card's `name` and the server name at `initialize`; empty publishes `Beacon MCP` (`Beacon` on the card and at `initialize`) |
| `ResourceDocumentation` | string | empty | The `resource_documentation` URL, also the server card's `documentation`; empty publishes Beacon's MCP docs. Anything but an absolute https URL logs a startup warning |

Without the section, the authority published is `Beacon:Authentication:Oidc:Authority` when SSO is enabled. Startup warns about a derived issuer that is not absolute, and about an external server published without `ScopesSupported`.

## Telemetry

| Key | Type | Default | What it does |
|---|---|---|---|
| `Beacon:Telemetry:CaptureContent` | bool | `false` | Put an MCP tool's input on the current trace span (`beacon.tool.input`), only where the project's content retention allows. Logs and metrics never carry content |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | string | unset | Sample host: when set, registers OpenTelemetry tracing and metrics with the OTLP exporter (`/beacon/api/health` and notification calls are not traced) |
| `OTEL_SERVICE_NAME` | string | `beacon` | Sample host: the service name |

Other standard `OTEL_*` variables are read by the OpenTelemetry SDK itself. The Beacon libraries choose no exporter.

## Embed tokens (`Beacon:EmbedToken`)

Read by `IEmbedTokenService`, a host-facing service that mints and validates HS256 tokens for embeddable integrations; no Beacon route uses it. Validated at first use.

| Key | Type | Default | What it does |
|---|---|---|---|
| `SigningKey` | string | Required | Required; at least 32 bytes (UTF-8). Supply as a secret |
| `Issuer` | string | `beacon` | Must not be blank |
| `DefaultTtlSeconds` | int | `300` | Greater than 0, at most `MaxTtlSeconds` |
| `MaxTtlSeconds` | int | `900` | Greater than 0 |

## Scheduling

Beacon does not bundle a job runner; recurring and background work goes through `IBeaconScheduler`, registered with `options.AddBeaconScheduler<T>()` (required). The sample host implements it on Moberg.Warp.

```csharp
namespace Beacon.Core.Worker;

public interface IBeaconScheduler
{
    Task AddOrUpdate(int subscriptionId, string subscriptionName, string cron);
    Task Remove(int subscriptionId, string subscriptionName);

    // Optional: default implementations throw NotImplementedException.
    Task AddOrUpdateDataQualityJob(int contractId, string contractName, string cron);
    Task RemoveDataQualityJob(int contractId, string contractName);
    Task<string> EnqueueProjectDocumentation(int projectId, int userId, string? notifyUserId);
    Task<string> EnqueueAiActorThinkCycle(int actorId, int subscriptionId);
    Task<string> EnqueueMcpEval(int runId);
}
```

Beacon calls `AddOrUpdate` when a subscription is created or its cron changes and `Remove` when it is deleted or disabled; your implementation maps them onto recurring jobs that call `IJobService.ExecuteQuery(subscriptionId)`. The `Enqueue…` methods return the job id the UI correlates `JobStatusChanged` events by. A host that runs Beacon's jobs on its own job host must mark each job's DI scope as system work (`ISystemWorkScope.MarkAsSystemWork()`). See [Background jobs](/integrate/background-jobs/).

## Data-source connectors

Register a connector per engine on the builder: `.AddPostgreSqlConnector()`, `.AddSqlServerConnector()`, `.AddMySqlConnector()`, `.AddBigQueryConnector()`, `.AddSnowflakeConnector()`, `.AddDatabricksConnector()`, `.AddAzureSynapseConnector()`, `.AddCloudWatchConnector()`, `.AddApiConnector()`. Data-source connections are entered in the UI and encrypted at rest. Give every data source Beacon reads a read-only login. On PostgreSQL and MySQL, Beacon also runs each read in a read-only transaction. See [Data-source connectors](/integrate/connectors/) and [Data sources](/features/data-sources/).

### Warehouse cost limits

Each BigQuery query job is sent with `MaximumBytesBilled`: the data source's own `MaximumBytesBilled`, or 10 GiB
(10,737,418,240 bytes) when it sets none. A job that would bill more fails without a charge, with a message naming the
limit. The per-source value is a number of bytes in the data source's connection JSON, next to `ProjectId` and
`ServiceAccountJson` (for example `"MaximumBytesBilled": 2147483648`); the **New data source** dialog does not ask for
it, so set it by replacing the connection with `PUT /beacon/api/data-sources/{id}` (with `confirmTargetChange: true`,
see [Data sources](/features/data-sources/)). The deployment can lower every source's limit, bound from
`Beacon:DataSources:BigQuery`:

| Key | Type | Default | What it does |
|---|---|---|---|
| `MaxBytesBilled` | long | none | The most bytes one BigQuery query job may bill; a larger source limit is lowered to it. At least 1 when set, validated at startup |

```json
{ "Beacon": { "DataSources": { "BigQuery": { "MaxBytesBilled": 5368709120 } } } }
```

Not configurable, on every deployment:

- A BigQuery, Databricks or CloudWatch data source's stored `QueryTimeoutSeconds` is clamped to 900 seconds (0 or less
  means 300). A BigQuery job and a Databricks statement are cancelled on the warehouse when the timeout passes or the
  caller cancels.
- Databricks results are limited to 16 MiB (`byte_limit`); a larger result fails rather than being cut short. A BigQuery
  or Databricks result read without a row cap fails above 10,000 rows (Beacon's own reads always carry one).
- A REST API data source's `TimeoutSeconds` is clamped to 1–120 seconds and one response may hold at most 10 MB. Fetching
  the OpenAPI description for a metadata import takes at most 30 seconds and 50 MB.

## Logging

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore": "Warning"
    }
  }
}
```

Beacon logs identifiers and counts, never query text, connection strings, row data or tokens. Security log events are structured log events 7101 to 7123; route them to your log pipeline, together with the `Beacon.Audit` category, which carries the MCP audit events and a copy of every committed [security audit](/security/audit-trail/#shipping-to-a-siem) event (9104 to 9110).

## Complete example

```json
{
  "ConnectionStrings": {
    "BeaconContext": "Host=db;Database=beacon;Username=beacon;Pooling=true;MaxPoolSize=50"
  },
  "Beacon": {
    "BaseUrl": "https://beacon.example.com/beacon",
    "PublicBaseUrl": "https://beacon.example.com",
    "DataProtection": { "ApplicationName": "beacon-production" },
    "Authentication": {
      "Session": { "RevalidateMinutes": 5, "AbsoluteLifetimeHours": 12 },
      "Oidc": {
        "Enabled": true,
        "Authority": "https://login.microsoftonline.com/{tenant-id}/v2.0",
        "ClientId": "{client-id}",
        "AllowedTenants": [ "{tenant-id}" ]
      }
    },
    "UserManagement": {
      "LocalLogin": { "Enabled": false, "BreakGlassUserNames": [ "superadmin" ] }
    },
    "Authorization": {
      "RequireProjectMembership": true,
      "SeparationOfDuties": { "Enabled": true }
    },
    "ApiKeys": { "MaxLifetimeDays": 180 },
    "Mcp": { "Audit": { "Required": true, "RetentionDays": 400 } },
    "ForwardedHeaders": { "Enabled": true, "KnownProxies": [ "10.0.0.5" ] },
    "LLM": { "Provider": "OpenAI", "Model": "<model-id>" }
  },
  "Logging": { "LogLevel": { "Default": "Information" } }
}
```

Supply `Beacon__EncryptionKey`, `Beacon__Authentication__Oidc__ClientSecret`, `Beacon__UserManagement__SetupToken`, `Beacon__LLM__ApiKey` and the database password as environment variables or from a secrets provider.

## Next steps

- [Host wiring](/integrate/host-setup/): registration and middleware in context
- [Production checklist](/integrate/production/)
- [Security model](/security/)
