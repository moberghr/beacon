---
title: Host wiring
description: "A complete, annotated Program.cs for embedding Beacon: service registration, the load-bearing middleware order, endpoint mapping, startup migration, and every option of AddBeaconServices."
---

This page gives you a complete `Program.cs` and explains each part. Copy it, then remove what you do not use. The reference host in the repository, `src/Beacon.SampleProject/Program.cs`, follows the same shape.

## Configuration

Two values are required before anything starts. Keep both out of source control: user-secrets in development, a secret store or environment variables everywhere else.

| Key | Environment variable | Value |
|---|---|---|
| `ConnectionStrings:BeaconContext` | `ConnectionStrings__BeaconContext` | Connection string of Beacon's metadata database |
| `Beacon:EncryptionKey` | `Beacon__EncryptionKey` | Base64 of at least 32 random bytes: `openssl rand -base64 32` |

```bash
dotnet user-secrets init
dotnet user-secrets set "ConnectionStrings:BeaconContext" "Host=localhost;Database=beacon;Username=beacon;Password=…"
dotnet user-secrets set "Beacon:EncryptionKey" "$(openssl rand -base64 32)"
```

The database must exist; Beacon creates its schema and tables in it (see [Metadata database](/integrate/database/)).

Beacon checks the encryption key in every environment and refuses to start when it is missing, shorter than 32 bytes, not random-looking (all printable ASCII, or fewer than 16 distinct byte values), a published example value, or an unexpanded `${…}` placeholder. Outside `Development` you also need `Beacon:DataProtection:ApplicationName` (see [Data Protection](#data-protection)). The [configuration reference](/getting-started/configuration/) lists every other key.

## The complete Program.cs

This host keeps Beacon's metadata in PostgreSQL, monitors PostgreSQL, signs users in with Beacon's own user store (plus optional OIDC), runs jobs on Warp, and exposes the MCP server. `BeaconScheduler` and `BeaconClaimsTransformation` are small classes you add to your project. See [Background jobs](/integrate/background-jobs/) and [the claims transformation](#the-claims-transformation) below.

```csharp
using Beacon.AI;                              // AddBeaconAI
using Beacon.Api;                             // AddBeaconApiServices
using Beacon.Api.Authentication;              // cookie/OIDC/JWT/Data Protection helpers + middleware
using Beacon.Api.Endpoints;                   // AddBeaconApiAuthorization, MapBeaconApi, MapLoginEndpoints, MapSetupEndpoints
using Beacon.Connector.PostgreSql;            // AddPostgreSqlConnector
using Beacon.Core;                            // AddBeaconServices, BeaconConfiguration
using Beacon.Core.Authentication.Providers;   // DatabaseAuthenticationProvider
using Beacon.Core.PostgreSql;                 // UsePostgreSql
using Beacon.MCP;                             // AddBeaconMcp, MapBeaconMcp
using Beacon.MCP.Discovery;                   // MapMcpDiscovery, UseMcpResourceMetadataChallenge
using Beacon.UI;                              // MapBeaconUi, BeaconUiEndpointRouteBuilderExtensions.BasePath

var builder = WebApplication.CreateBuilder(args);
const string beaconBasePath = BeaconUiEndpointRouteBuilderExtensions.BasePath; // "/beacon"

// AI work (documentation, ask) can run for minutes; keep server and HttpClient timeouts long.
builder.WebHost.ConfigureKestrel(x =>
{
    x.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
    x.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddHttpClient().ConfigureHttpClientDefaults(x => x.ConfigureHttpClient(y => y.Timeout = TimeSpan.FromMinutes(5)));
// Beacon's own routes get body-size and request-time bounds from Beacon:RequestLimits (getting-started/configuration);
// the 5-minute values above stay, AI calls need them. A shorter RequestHeadersTimeout (30 s) is fine: headers arrive at once.

// Your job runner (here: Warp). See "Background jobs".
builder.Services.AddBeaconWarp(builder.Configuration);

// Claims transformation (the SignalR user id provider comes with AddBeaconApiServices).
builder.Services.AddBeaconHostInfrastructure<BeaconClaimsTransformation>();

// 1. Beacon core, connectors, metadata database (UsePostgreSql ends the chain).
builder.Services.AddBeaconServices(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();             // required
        options.BaseUrl = "https://beacon.example.com/beacon";       // deep links in notifications; include /beacon
        options.UseAI = true;                                        // needs AddBeaconAI below
        options.Authorization.Enabled = true;
        options.Authentication.EnableLoginForm = true;
        options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();
        options.EnableUserManagement();
        // options.EnableApprovalWorkflow(x => x.RequireApprovedVersionToRun = true);  // four-eyes, opt-in
        // options.AddEmailAdapter<SmtpEmailAdapter>();                                  // your IEmailAdapter
    })
    .AddPostgreSqlConnector()
    .UsePostgreSql();                                                // ConnectionStrings:BeaconContext

// 2. Sign-in.
builder.Services.AddBeaconCookieAuthentication(beaconBasePath);
builder.Services.AddBeaconDataProtection(builder.Configuration, builder.Environment);
    // Outside Development chain .ProtectKeysWithCertificate(...) or .ProtectKeysWithAzureKeyVault(...) here.
builder.Services.AddBeaconOidcAuthentication(builder.Configuration);   // no-op unless Beacon:Authentication:Oidc:Enabled

// 3. AI and MCP (optional).
builder.Services.AddBeaconAI(builder.Configuration);
builder.Services.AddBeaconMcp();

// 4. REST API, policies, realtime, antiforgery, OpenAPI.
builder.Services.AddAntiforgery(x =>
{
    x.HeaderName = "X-XSRF-TOKEN";
    x.Cookie.Name = ".Beacon.Antiforgery";
});
builder.Services.AddBeaconApiAuthorization();   // BeaconApi, Admin, Execute-scope and permission policies
builder.Services.AddBeaconApiServices();        // SignalR hub + notifiers; x => x.Realtime = false to turn off
builder.Services.AddOpenApi();

var app = builder.Build();
var beaconConfiguration = app.Services.GetRequiredService<BeaconConfiguration>();

// Beacon does not migrate itself: create the schema and apply migrations at startup.
Beacon.Core.ServiceConfiguration.UseBeacon(app.Services, createSchema: true);

// ── Middleware: the order below is load-bearing ──────────────────────────
app.UseHttpsRedirection();                             // see "HTTPS redirection" below for local MCP over HTTP
app.UseBeaconSecurityHeaders();                        // CSP, nosniff, framing and referrer headers on Beacon's routes
app.UseStaticFiles();                                  // serves the UI bundle
app.UseMcpResourceMetadataChallenge();                 // adds resource_metadata to 401s from /beacon/mcp
app.UseBeaconAuthentication();                         // API key, bearer JWT, UseAuthentication, Beacon.Auth cookie, origin check
app.UseAuthorization();
app.UseMiddleware<LoginFormAuthMiddleware>(beaconConfiguration, beaconBasePath);
app.UseMiddleware<FirstRunSetupMiddleware>(beaconConfiguration, beaconBasePath);
app.UseAntiforgery();

// ── Endpoints ────────────────────────────────────────────────────────────
app.MapBeaconOpenApi();                                // /openapi/v1.json (signed-in callers only outside Development)
app.MapBeaconApi();                                    // /beacon/api/* and the /beacon/api/hub SignalR hub
app.MapLoginEndpoints(beaconBasePath, beaconConfiguration);  // /beacon/api/auth/login, logout, sso/challenge
app.MapSetupEndpoints(beaconBasePath);                 // /beacon/api/setup/* (first-run admin)
app.MapBeaconMcp();                                    // /beacon/mcp, Execute scope required
app.MapMcpDiscovery();                                 // /.well-known/oauth-protected-resource, server card
app.MapBeaconUi();                                     // React app at /beacon

await app.StartBeaconWarpAsync();                      // your job runner's startup (see "Background jobs")
app.Run();
```

`AddBeaconWarp` and `StartBeaconWarpAsync` are two small extension methods you write once; [Background jobs](/integrate/background-jobs/) shows them. With another job runner, replace both lines.

`AddOpenApi()` and `MapOpenApi()` need a direct reference to `Microsoft.AspNetCore.OpenApi` in your project (`dotnet add package Microsoft.AspNetCore.OpenApi`); getting it only through `Moberg.Beacon.Api` fails the build with `CS9137`. Drop both lines if you do not want an OpenAPI document.

## What each part does

### `AddBeaconServices`

```csharp
public static BeaconBuilder AddBeaconServices(
    this IServiceCollection services,
    IConfiguration configuration,
    Action<BeaconConfiguration> beaconConfiguration)
```

It registers Beacon's core services and returns a `BeaconBuilder`. Chain connectors and host extensions on the builder, and finish with `UsePostgreSql(...)` or `UseSqlServer(...)`. Those return `IServiceCollection`, so they end the chain.

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options => { /* … */ })
    .AddPostgreSqlConnector()             // connectors: BeaconBuilder → BeaconBuilder
    .ExposeDbContext<ShopDbContext>(…)    // host extensions: BeaconBuilder → BeaconBuilder
    .UsePostgreSql();                     // metadata DB: BeaconBuilder → IServiceCollection (last)
```

`BeaconConfiguration` is set **in code** through the lambda; it is not bound from `appsettings.json`.

| Option | Default | What it does |
|---|---|---|
| `AddBeaconScheduler<T>()` | None (required) | Your `IBeaconScheduler`. Without one, `AddBeaconServices` throws `Implementation of IBeaconScheduler is required.` |
| `BaseUrl` | `null` | Public URL of the UI, **including `/beacon`** (e.g. `https://beacon.example.com/beacon`). Notification messages link to `{BaseUrl}/notifications/{id}`. Editable later in **Admin settings**. |
| `UseAI` | `false` | Turns AI features on. Also call `AddBeaconAI`. |
| `Authorization.Enabled` | `false` | Role-based authorization. Turn it on in every real deployment. |
| `AddAuthorizationProvider<T>()` | user store when user management is on, otherwise allow-all | Custom `IBeaconAuthorizationProvider` (global read/write ceiling per user). |
| `Authentication.EnableLoginForm` | `false` | Beacon's login page and the redirect of signed-out browsers to `/beacon/login`. |
| `AddAuthenticationProvider<T>()` | user store when user management is on | Password check. `DatabaseAuthenticationProvider` uses Beacon's user store. |
| `Authentication.CookieExpirationHours` / `RememberMeExpirationDays` | `24` / `30` | Requested cookie lifetime; always capped by the session's absolute lifetime (12 h by default). |
| `Authentication.LoginRedirectPath` | `/beacon/home` | Where the login page sends a user after sign-in. |
| `EnableUserManagement(x => …)` | off | Beacon's user store: users, roles, first-run setup, password policy (`MinimumPasswordLength` 8, `RequirePasswordComplexity` true, `AllowInternalUsers` true). |
| `EnableApprovalWorkflow(x => …)` | off | Four-eyes review of query changes and, by default, migration jobs, Custom SQL rules and recipient destinations; data-source connections and the LLM connection when switched on. See [Approvals](/security/approvals/). |
| `AddEmailAdapter<T>()` | none | Your `IEmailAdapter`. Without one there is no email channel. |
| `SavedQueryTools.NamedToolLimit` | `25` | Above this many published saved queries, MCP lists two generic tools instead of one tool per query. |

Only two parts of user management are also read from configuration: `Beacon:UserManagement:SetupToken` and `Beacon:UserManagement:LocalLogin:*`.

### The metadata database

`UsePostgreSql()` and `UseSqlServer()` read `ConnectionStrings:BeaconContext`. Overloads take a connection string, or, on PostgreSQL, your own `NpgsqlDataSource`. The schema is always `beacon`. Details: [Metadata database](/integrate/database/).

### Authentication helpers

| Call | What it does |
|---|---|
| `AddBeaconHostInfrastructure<TClaimsTransformation>()` | Registers your claims transformation. |
| `AddBeaconCookieAuthentication(basePath)` | The `Beacon.Auth` cookie (HttpOnly, SameSite=Lax, Secure on HTTPS), server-side session revalidation, JSON 401/403 for API and MCP calls instead of redirects. **Pass `"/beacon"`**, so the login path is `/beacon/login`. |
| `AddBeaconDataProtection(configuration, environment)` | Stores the Data Protection key ring in Beacon's database. Skip it if your host already configures Data Protection. |
| `AddBeaconOidcAuthentication(configuration)` | OIDC sign-in from `Beacon:Authentication:Oidc` (does nothing unless `Enabled`). |
| `AddBeaconJwtAuthentication(jwt => …)` | Bearer JWTs for the REST API and MCP (code-configured). |

All options are on [Sign-in & identity](/integrate/authentication/).

### API services

| Call | What it does |
|---|---|
| `AddBeaconApiAuthorization()` | Registers the `BeaconApi`, `BeaconApiAdmin` and Execute-scope policies plus one policy per functional permission. Required by `MapBeaconApi` and `MapBeaconMcp`. |
| `AddBeaconApiServices()` | SignalR with Beacon's hub user-id provider, the login and request rate limiters (`Beacon:RateLimits`), notifiers, and the request principal Beacon's core reads the caller from (`IBeaconRequestPrincipal`; `AddBeaconCookieAuthentication` and `AddBeaconMcp` register it too, and startup fails when Beacon's endpoints are mapped without it). Options: `Realtime` (default `true`), `ConfigureSignalR` (e.g. a Redis backplane), `ExternalLogin` (when your app owns sign-in). |
| `AddAntiforgery(...)` | The UI sends the antiforgery token in `X-XSRF-TOKEN`; it gets it from `GET /beacon/api/csrf`. API-key and bearer callers are exempt. |

:::caution[Upgrading to 5.0: the request principal]
In 5.0 an `IHttpContextAccessor` alone no longer tells Beacon's core who the request's caller is. A web host calls `AddBeaconApiServices`, `AddBeaconCookieAuthentication` or `AddBeaconMcp` (each registers the request principal), or registers its own with `UseRequestPrincipal<T>()`. A host that registers only Beacon's core and an accessor serves every request as if no request were in progress.
:::

```csharp
// Realtime off: no SignalR, no hub route; the UI simply does not connect.
builder.Services.AddBeaconApiServices(x => x.Realtime = false);

// Several instances: a SignalR backplane.
builder.Services.AddBeaconApiServices(x =>
    x.ConfigureSignalR = signalR => signalR.AddStackExchangeRedis(redisConnectionString));

// Your app owns sign-in: the UI sends signed-out users to your login page.
builder.Services.AddBeaconApiServices(x =>
    x.ExternalLogin = new ExternalLoginOptions { LoginUrl = "/account/login", ReturnUrlParameter = "returnUrl" });
```

The hub addresses a signed-in session by its `NameIdentifier`, the user's `Users.ExternalId` (the `UserId` of `IBeaconActorAccessor`'s actor). Publish your own events with `IHubContext<BeaconHub>.Clients.User(actor.UserId)`. API keys and MCP callers get no hub user id. Register your own `IUserIdProvider` before `AddBeaconApiServices()` to replace Beacon's.

## The middleware pipeline

The order is load-bearing. Each step depends on the ones before it:

| # | Middleware | Why it sits here |
|---|---|---|
| 1 | `UseForwardedHeaders` (behind a proxy) | Everything after it must see the client's address and scheme: the login throttle is per IP, the cookie's Secure flag follows the scheme, the origin check compares the scheme and Host. |
| 2 | `UseHttpsRedirection` | API keys travel in headers; never accept them over plain HTTP outside development. |
| 3 | `UseBeaconSecurityHeaders` | Security response headers on Beacon's routes, before `UseStaticFiles` so the UI bundle carries them too. `UseBeaconAuthentication` adds them itself when you leave this out. See [Security headers and the origin check](#security-headers-and-the-origin-check). |
| 4 | `UseStaticFiles` | Serves the UI bundle before authentication runs. |
| 5 | Your exception handler (optional) | Turns Beacon's exceptions into problem+json. See [below](#error-responses). |
| 6 | `UseMcpResourceMetadataChallenge` | Decorates every 401 from `/beacon/mcp` with the RFC 9728 `resource_metadata` pointer; must precede the middleware that writes those 401s. |
| 7 | `UseAuthentication` (`UseBeaconAuthentication` wires steps 7 to 9) | Your own schemes. A remote sign-in callback (the OIDC form post) is answered here, before the origin check. |
| 8 | Beacon's authentication | One step: a presented API key (`Authorization: Bearer sk-sem_…`) or bearer JWT (Entra and other OIDC access tokens, when enabled), else the `Beacon.Auth` cookie, fills `HttpContext.User` **before** authorization evaluates policies. A presented key or token wins over any session, so the key's scope and projects apply and an MCP client is never redirected to a login page. |
| 9 | Origin check | Checks the Host and `Origin` of state-changing, realtime-hub and anonymous requests (`Beacon:Security`). |
| 10 | `UseAuthorization` | Endpoint policies. |
| 11 | `LoginFormAuthMiddleware` | Redirects signed-out browsers to `/beacon/login` (only when `EnableLoginForm`). |
| 12 | `FirstRunSetupMiddleware` | Until the first admin exists, redirects Beacon's requests to `/beacon/setup`, except the health check `/beacon/api/health` and the MCP discovery documents (only with user management). |
| 13 | `UseAntiforgery` | After authentication, so tokens are issued for the signed-in user. |

A refused API key is answered with a 401 problem (`application/problem+json`) on every Beacon route, also an anonymous one. A refused bearer token is answered with a 401 problem carrying `error: "invalid_token"` (and, for 5.x clients, `message`) and `WWW-Authenticate: Bearer error="invalid_token"` on an API request; on a page it is ignored, so the session cookie or the login redirect applies. Beacon registers its schemes under the names in `BeaconAuthenticationDefaults` (the `Beacon` policy scheme forwards to the one the request's credential belongs to); they never take away a host's only scheme of its own as its default.

Do not call `UseAuthentication()` again after `UseBeaconAuthentication()`: a second pass of your schemes would replace the API key or token Beacon's step accepted.

Steps 3, 8, 9, 11 and 12 act on `/beacon` and the prefixes in `Beacon:Authentication:AdditionalPaths` only: a Beacon API key, bearer token or `Beacon.Auth` cookie never authenticates another route of your application, and your routes are never redirected to Beacon's login or setup page. Beacon's cookie scheme refuses the session on those other routes as well, so a host whose default scheme is the cookie still never has its own routes authenticated by it. List `/warp` there when you serve the Warp dashboard with Beacon's admin check: a host upgraded from a version without path scoping must add it, or the dashboard refuses every request. See [Where Beacon credentials apply](/getting-started/configuration/#where-beacon-credentials-apply).

:::danger[Do not reorder or assign `context.User` later]
Beacon's authentication records its result as the request's authentication result; SignalR uses it to close connections when a session ends. Reordering it, or assigning `context.User` after it, breaks API-key callers, the login redirect and session revocation. `ApiKeyAuthMiddleware` and `BeaconCookieAuthMiddleware` are obsolete: call `UseBeaconAuthentication` instead.
:::

## Endpoints

| Call | Maps |
|---|---|
| `MapBeaconApi()` | `/beacon/api/*` (cookie or API key, `BeaconApi` policy, antiforgery and permission filters) and the SignalR hub `/beacon/api/hub`. Migration endpoints only when `Beacon:Migrations:Enabled`. |
| `MapLoginEndpoints("/beacon", configuration)` | `POST /beacon/api/auth/login` (only when password sign-in is offered), `POST /beacon/api/auth/logout`, `GET /beacon/api/auth/sso/challenge`. |
| `MapSetupEndpoints("/beacon")` | `GET /beacon/api/setup/status`, `POST /beacon/api/setup/superadmin`, `GET /beacon/api/setup/roles`. Extension on `WebApplication`. Map it only with user management on. |
| `MapBeaconMcp()` | `/beacon/mcp` with the Execute-scope policy. Never map MCP yourself with `MapMcp(...)` and a bare `RequireAuthorization()`. |
| `MapMcpDiscovery()` | Anonymous `/.well-known/oauth-protected-resource`, `/.well-known/oauth-protected-resource/beacon/mcp`, `/.well-known/mcp/server-card.json`. |
| `MapBeaconUi()` | The React app at `/beacon` and its client routes. Your app keeps `/`. |
| `MapBeaconOpenApi()` | Your OpenAPI document, which includes Beacon's endpoints. Optional. Same as `MapOpenApi()`, but outside Development it needs a signed-in Beacon caller (list `/openapi` in `Beacon:Authentication:AdditionalPaths`) unless `Beacon:OpenApi:Anonymous` is `true`. A host that calls `MapOpenApi()` itself keeps it as it is. |

The full route list is on [Routes & endpoints](/reference/routes/).

## The claims transformation

`AddBeaconHostInfrastructure<T>()` needs an `IClaimsTransformation`. With Beacon's user store, this one adds the user id and name claims and resolves a global role from the store by the session's external id, never from a name the user typed. It behaves like the reference host's `SampleClaimsTransformation`; Beacon's own role constants are internal, so it carries the role names and levels itself:

```csharp
using System.Security.Claims;
using Beacon.Core.Authorization;
using Beacon.Core.Models.UserManagement;
using Beacon.Core.Services;
using Microsoft.AspNetCore.Authentication;

public sealed class BeaconClaimsTransformation(IUserManagementService users) : IClaimsTransformation
{
    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
        {
            return principal;
        }

        var name = identity.Name ?? "unknown";
        if (!identity.HasClaim(x => x.Type == BeaconClaims.UserId))
        {
            identity.AddClaim(new Claim(BeaconClaims.UserId, name));
        }

        if (!identity.HasClaim(x => x.Type == BeaconClaims.UserName))
        {
            identity.AddClaim(new Claim(BeaconClaims.UserName, name));
        }

        var externalId = identity.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (identity.HasClaim(x => x.Type == BeaconClaims.Role) || string.IsNullOrWhiteSpace(externalId))
        {
            return principal;
        }

        var user = await users.GetUserByExternalIdAsync(externalId);
        var role = user is { IsEnabled: true } ? ResolveRole(user) : null;
        if (role != null)
        {
            identity.AddClaim(new Claim(BeaconClaims.Role, role));
        }

        return principal;
    }

    private static string? ResolveRole(BeaconUserData user)
    {
        if (user.IsSuperAdmin)
        {
            return "Admin";
        }

        if (user.Roles.Count == 0)
        {
            return null;
        }

        // Role levels: Admin 3, Editor 2, Viewer 1. Functional roles (level 0) carry no global role.
        var level = user.Roles.Max(x => x.Level);
        return level switch
        {
            >= 3 => "Admin",
            2 => "Editor",
            1 => "Viewer",
            _ => null,
        };
    }
}
```

## Error responses

Beacon's handlers throw `InvalidOperationException` for business-rule violations, `BeaconException` for domain errors and `SettingLockedException` when a deployment lock refuses a change; access refusals are already mapped to 403/404 by `MapBeaconApi`. Map the rest to problem+json for `/beacon/api` so the UI shows the message instead of a generic 500:

```csharp
using Beacon.Core.Models;

app.UseWhen(x => x.Request.Path.StartsWithSegments("/beacon/api"), branch => branch.Use(async (context, next) =>
{
    try
    {
        await next(context);
    }
    catch (Exception ex) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested)
    {
        var (status, title, type) = ex switch
        {
            UnauthorizedAccessException or BeaconForbiddenException => (403, "Forbidden", "/errors/forbidden"),
            SettingLockedException x => (409, x.Message, "/errors/setting-locked"),
            InvalidOperationException x => (400, x.Message, "/errors/invalid-operation"),
            BeaconException x => (400, x.Message, "/errors/beacon"),
            _ => (500, "Internal server error", "/errors/internal"),
        };
        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(new { type, title, status }, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
    }
}));
```

Place it after `UseStaticFiles` and before the authentication middleware, so their exceptions are mapped too.

## Data Protection

The `Beacon.Auth` cookie and antiforgery tokens are protected with ASP.NET Core Data Protection, which belongs to your host:

- **Your host already configures Data Protection** (shared key ring, Azure Blob, Redis): skip `AddBeaconDataProtection`.
- **Otherwise** call `AddBeaconDataProtection(configuration, environment)`. It keeps the key ring in Beacon's database (`data_protection_keys`) under the application name `Beacon:DataProtection:ApplicationName`, which is required outside `Development` and takes one name per environment.

Outside `Development` the key ring must be encrypted at rest or the host does not start. Chain a protector:

```csharp
builder.Services.AddBeaconDataProtection(builder.Configuration, builder.Environment)
    .ProtectKeysWithCertificate(certificate);          // or .ProtectKeysWithAzureKeyVault(keyId, credential)
```

`Beacon:DataProtection:AllowUnencryptedKeys=true` acknowledges an unencrypted ring instead (logged as a warning). With encryption on, keys that were stored unencrypted are revoked at startup, so everyone signs in once more.

## HTTPS redirection

`UseHttpsRedirection` belongs in every deployment: API keys travel in headers. On a development machine running plain HTTP, local MCP clients connect over HTTP, so the reference host skips the redirect for `/beacon/mcp` in Development only:

```csharp
if (app.Environment.IsDevelopment())
{
    app.UseWhen(x => !x.Request.Path.StartsWithSegments("/beacon/mcp"), y => y.UseHttpsRedirection());
}
else
{
    app.UseHttpsRedirection();
}
```

## Security headers and the origin check

Beacon protects its own routes in the browser; your routes keep whatever your application sends. Both act on `/beacon`
and `Beacon:Authentication:AdditionalPaths` only.

**Response headers.** Every Beacon response carries `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`,
`Referrer-Policy: strict-origin` and a `Permissions-Policy` that turns off camera, microphone, geolocation, payment and
similar device features. Responses under `/beacon` also carry a Content-Security-Policy that fits the shipped UI:

```text
default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:;
connect-src 'self'; worker-src 'self'; object-src 'none'; base-uri 'self'; form-action 'self'; frame-ancestors 'none'
```

Scripts, styles, fonts and workers load only from Beacon's own origin, nothing loads from a CDN, and no page can frame
Beacon. `style-src` allows inline styles because the SQL editor and the diagram renderer inject style elements.
`Beacon:Security:Csp` is `Enforce` (default), `ReportOnly` (sent as `Content-Security-Policy-Report-Only`, which the
browser only reports in its console) or `Off`. A header your application or a proxy already set on the response is
kept, so you can send a policy of your own.

**Origin check.** It checks the requests a page on another origin could send: `POST`, `PUT`, `PATCH` and `DELETE`, the
realtime hub at `/beacon/api/hub` (negotiate and the WebSocket upgrade) and Beacon's anonymous endpoints (sign-in,
first-run setup, health). Their `Origin` must be an allowed origin; without an `Origin`, the browser's `Sec-Fetch-Site`
must not be `cross-site` or `same-site`. The allowed origins are `Beacon:Security:AllowedOrigins` plus the origin of
`Beacon:PublicBaseUrl`; when neither is set, the request's own scheme and Host. With either set, a browser request must
also arrive with one of their hosts, so Beacon answers browsers only under the names you configured. Clients that are not
browsers (API keys, MCP clients, health probes) send neither header and are not affected, and a request that carries an
`Authorization` header (API key, bearer token) is not checked at all: a page on another origin cannot add one without a
CORS preflight, so only cookie-authenticated requests need the check.

Set `Beacon:Security:AllowedOrigins` or `Beacon:PublicBaseUrl` before you enforce: without them the check compares the
`Origin` with the request's own scheme and Host (behind a TLS-terminating proxy that does not forward the scheme, every
browser write is refused, and Beacon logs a startup warning in `Enforce`), and the Host check does not run, so there is no
DNS-rebinding protection. In the MCP gateway the check runs before routing, so the anonymous-endpoint rule does not apply
there; the state-changing and hub rules do. Configured hosts are compared in their punycode form.

`Beacon:Security:OriginGuard` is `LogOnly` (default for this release: a request it would refuse goes on and is logged as a
warning naming the method, path and reason) or `Enforce` (answered 403 problem+json). Run a release in `LogOnly`, look
for the warnings, then switch to `Enforce`.

```json
{
  "Beacon": {
    "PublicBaseUrl": "https://beacon.example.com",
    "Security": {
      "Csp": "Enforce",
      "OriginGuard": "Enforce",
      "AllowedOrigins": [ "https://beacon.example.com" ]
    }
  }
}
```

Behind a reverse proxy, set `Beacon:PublicBaseUrl` (or list the public origin in `AllowedOrigins`), and let the proxy
forward the public `Host` or apply forwarded headers: with configured origins, a browser request that arrives with the
proxy's internal host name is refused under `Enforce`. An origin is a scheme, a host and an optional port, with no path;
an entry that is not fails startup.

**HSTS** is yours: `Strict-Transport-Security` binds the whole host name, so send it from your application
(`app.UseHsts()` outside Development) or the proxy. Set `AllowedHosts` to your real host names as well, so the
framework's host filtering refuses other names on every route.

## The fluent chain

Since 5.x the same host can be wired with one chain on the services and two calls on the app. The chain is the preferred way to wire Beacon. The calls above keep working: `AddBeaconServices`, `AddBeaconAI` and `AddBeaconMcp` stay supported and become obsolete in a later release.

```csharp
using Beacon.AI;            // AddAI
using Beacon.Api;           // AddApi
using Beacon.Api.Authentication;   // AddCookieAuth
using Beacon.Gateway;       // UseBeaconPipeline, MapBeacon (package Moberg.Beacon.Gateway)
using Beacon.MCP;           // AddMcp

builder.Services.AddBeacon(builder.Configuration, options =>
    {
        options.AddBeaconScheduler<BeaconScheduler>();
        options.Authentication.EnableLoginForm = true;
        options.EnableUserManagement();
    })
    .AddPostgreSqlConnector()
    .AddAI()                                   // switches AI on; never set UseAI yourself
    .AddMcp()                                  // after AddAI
    .AddApi()                                  // REST API, its policies, antiforgery (X-XSRF-TOKEN, .Beacon.Antiforgery)
    .AddCookieAuth<AppClaimsTransformation>()  // Beacon.Auth cookie at /beacon + AddBeaconHostInfrastructure
    .UsePostgreSql();                          // ends the chain: it returns IServiceCollection

var app = builder.Build();
app.UseStaticFiles();
// Your exception handler, if any, goes here: before UseBeaconPipeline (see Error responses).
app.UseBeaconPipeline();   // steps 6 to 13 of the pipeline table above, in that order
app.MapBeacon();           // API, sign-in, setup, MCP with the Execute policy, discovery documents, UI
```

| Call | Does |
|---|---|
| `AddBeacon(configuration, options)` | `AddBeaconServices` without `UseAI`: setting `UseAI = true` there throws, so AI is on only when `.AddAI()` registered it. |
| `.AddAI()` | Registers Beacon's AI services, sets `UseAI` and removes the AI-off stand-ins Core registered, so the real eval service and connection tester are the ones resolved. A second call throws, and so does a call beside `AddBeaconAI` or `UseAI = true`. |
| `.AddMcp(configure?)` | `AddBeaconMcp`. Throws when `.AddAI()` has not run or `AddBeaconMcp` already did. |
| `.AddApi(configure?)` | `AddBeaconApiServices`, `AddBeaconApiAuthorization` and antiforgery with the header the shell sends (`X-XSRF-TOKEN`) and the cookie name `.Beacon.Antiforgery`. These are the application's antiforgery options, shared with your own antiforgery. |
| `.AddCookieAuth<TClaims>()` | `AddBeaconHostInfrastructure<TClaims>()` and `AddBeaconCookieAuthentication("/beacon")`. |
| `app.UseBeaconPipeline()` | `UseMcpResourceMetadataChallenge` (with MCP), `UseBeaconAuthentication`, `UseAuthorization`, `LoginFormAuthMiddleware` (with `EnableLoginForm`), `FirstRunSetupMiddleware` (with user management), `UseAntiforgery` (with antiforgery registered). Call it once, in place of `UseAuthentication`/`UseAuthorization`; a second call throws. |
| `app.MapBeacon()` | With `.AddApi()`: `MapBeaconApi`, `MapLoginEndpoints` (with `.AddCookieAuth()`, since sign-in issues the `Beacon.Auth` cookie), `MapSetupEndpoints` (with user management) and `MapBeaconUi`. With `.AddMcp()`: `MapBeaconMcp` and `MapMcpDiscovery`. `MapOpenApi` stays yours. |

`UseForwardedHeaders`, `UseHttpsRedirection`, `UseStaticFiles`, your exception handler, OIDC, the bearer JWT setup and Data Protection stay host calls. Put the exception handler before `UseBeaconPipeline` (step 5 of the pipeline table): placed after it, exceptions from authentication, antiforgery and the redirects reach the client as raw 500s. Use either the chain or the older calls for AI and MCP, not both: once the chain is in use, `AddBeaconAI` and `AddBeaconMcp` throw, and `.AddAI()` or `.AddMcp()` beside them throw too. A host that embeds Beacon as an MCP gateway keeps `AddBeaconMcpGateway`, `UseBeaconMcpGateway` and `MapBeaconMcpGateway` (the gateway is a profile of the same chain with AI left off, so the evaluation harness keeps refusing); `UseBeaconPipeline` and `MapBeacon` throw on it.

## Minimal variants

**No MCP, no AI.** Drop the `Moberg.Beacon.AI` and `Moberg.Beacon.MCP` packages, `UseAI`, `AddBeaconAI`, `AddBeaconMcp`, `UseMcpResourceMetadataChallenge`, `MapBeaconMcp` and `MapMcpDiscovery`.

**Your app owns sign-in.** Leave user management and the login form off, set `BeaconApiOptions.ExternalLogin`, and make sure your authentication produces a principal with `ClaimTypes.NameIdentifier` and a Beacon role claim (`Admin`, `Editor` or `Viewer`). Without Beacon's user store, only the `Admin` role claim grants functional permissions unless `Beacon:Authorization:Roles:HonourHostRoleClaims` is `true`. See [Sign-in & identity](/integrate/authentication/#your-app-owns-sign-in).

**SQL Server metadata.** Replace `Moberg.Beacon.Core.PostgreSql` with `Moberg.Beacon.Core.SqlServer` and `UsePostgreSql()` with `UseSqlServer()`. Warp needs PostgreSQL for its own tables; with SQL Server, use another runner or give Warp a PostgreSQL database.
