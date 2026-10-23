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

// Claims transformation + SignalR user id provider.
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
app.UseStaticFiles();                                  // serves the UI bundle
app.UseMcpResourceMetadataChallenge();                 // adds resource_metadata to 401s from /beacon/mcp
app.UseBeaconAuthentication();                         // API key, bearer JWT, UseAuthentication, Beacon.Auth cookie
app.UseAuthorization();
app.UseMiddleware<LoginFormAuthMiddleware>(beaconConfiguration, beaconBasePath);
app.UseMiddleware<FirstRunSetupMiddleware>(beaconConfiguration, beaconBasePath);
app.UseAntiforgery();

// ── Endpoints ────────────────────────────────────────────────────────────
app.MapOpenApi();                                      // /openapi/v1.json
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
| `AddBeaconHostInfrastructure<TClaimsTransformation>()` | Registers your claims transformation and the SignalR user-id provider. |
| `AddBeaconCookieAuthentication(basePath)` | The `Beacon.Auth` cookie (HttpOnly, SameSite=Lax, Secure on HTTPS), server-side session revalidation, JSON 401/403 for API and MCP calls instead of redirects. **Pass `"/beacon"`**, so the login path is `/beacon/login`. |
| `AddBeaconDataProtection(configuration, environment)` | Stores the Data Protection key ring in Beacon's database. Skip it if your host already configures Data Protection. |
| `AddBeaconOidcAuthentication(configuration)` | OIDC sign-in from `Beacon:Authentication:Oidc` (does nothing unless `Enabled`). |
| `AddBeaconJwtAuthentication(jwt => …)` | Bearer JWTs for the REST API and MCP (code-configured). |

All options are on [Sign-in & identity](/integrate/authentication/).

### API services

| Call | What it does |
|---|---|
| `AddBeaconApiAuthorization()` | Registers the `BeaconApi`, `BeaconApiAdmin` and Execute-scope policies plus one policy per functional permission. Required by `MapBeaconApi` and `MapBeaconMcp`. |
| `AddBeaconApiServices()` | SignalR, the login rate limiter, notifiers, and the request principal Beacon's core reads the caller from (`IBeaconRequestPrincipal`; `AddBeaconCookieAuthentication` and `AddBeaconMcp` register it too, and startup fails when Beacon's endpoints are mapped without it). Options: `Realtime` (default `true`), `ConfigureSignalR` (e.g. a Redis backplane), `ExternalLogin` (when your app owns sign-in). |
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

## The middleware pipeline

The order is load-bearing. Each step depends on the ones before it:

| # | Middleware | Why it sits here |
|---|---|---|
| 1 | `UseForwardedHeaders` (behind a proxy) | Everything after it must see the client's address and scheme: the login throttle is per IP, the cookie's Secure flag follows the scheme. |
| 2 | `UseHttpsRedirection` | API keys travel in headers; never accept them over plain HTTP outside development. |
| 3 | `UseStaticFiles` | Serves the UI bundle before authentication runs. |
| 4 | Your exception handler (optional) | Turns Beacon's exceptions into problem+json. See [below](#error-responses). |
| 5 | `UseMcpResourceMetadataChallenge` | Decorates every 401 from `/beacon/mcp` with the RFC 9728 `resource_metadata` pointer; must precede the middleware that writes those 401s. |
| 6 | `ApiKeyAuthMiddleware` (`UseBeaconAuthentication` wires steps 6 to 9) | Authenticates `Authorization: Bearer sk-sem_…` before cookie auth could redirect an MCP client to a login page. |
| 7 | `UseBeaconJwtBearerAuthentication` | Bearer JWTs (Entra and other OIDC access tokens). |
| 8 | `UseAuthentication` | Your own schemes. |
| 9 | `BeaconCookieAuthMiddleware` | Fills `HttpContext.User` from the `Beacon.Auth` cookie **before** authorization evaluates policies. |
| 10 | `UseAuthorization` | Endpoint policies. |
| 11 | `LoginFormAuthMiddleware` | Redirects signed-out browsers to `/beacon/login` (only when `EnableLoginForm`). |
| 12 | `FirstRunSetupMiddleware` | Until the first admin exists, redirects Beacon's requests to `/beacon/setup`, except the health check `/beacon/api/health` and the MCP discovery documents (only with user management). |
| 13 | `UseAntiforgery` | After authentication, so tokens are issued for the signed-in user. |

Steps 6, 7, 9, 11 and 12 act on `/beacon` and the prefixes in `Beacon:Authentication:AdditionalPaths` only: a Beacon API key, bearer token or `Beacon.Auth` cookie never authenticates another route of your application, and your routes are never redirected to Beacon's login or setup page. Beacon's cookie scheme refuses the session on those other routes as well, so a host whose default scheme is the cookie still never has its own routes authenticated by it. List `/warp` there when you serve the Warp dashboard with Beacon's admin check: a host upgraded from a version without path scoping must add it, or the dashboard refuses every request. See [Where Beacon credentials apply](/getting-started/configuration/#where-beacon-credentials-apply).

:::danger[Do not reorder or assign `context.User` later]
`ApiKeyAuthMiddleware` and `BeaconCookieAuthMiddleware` record their result as the request's authentication result; SignalR uses it to close connections when a session ends. Reordering them, or assigning `context.User` after them, breaks API-key callers, the login redirect and session revocation.
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
| `MapOpenApi()` | Your OpenAPI document, which includes Beacon's endpoints. Optional. |

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

## Minimal variants

**No MCP, no AI.** Drop the `Moberg.Beacon.AI` and `Moberg.Beacon.MCP` packages, `UseAI`, `AddBeaconAI`, `AddBeaconMcp`, `UseMcpResourceMetadataChallenge`, `MapBeaconMcp` and `MapMcpDiscovery`.

**Your app owns sign-in.** Leave user management and the login form off, set `BeaconApiOptions.ExternalLogin`, and make sure your authentication produces a principal with `ClaimTypes.NameIdentifier` and a Beacon role claim (`Admin`, `Editor` or `Viewer`). Without Beacon's user store, only the `Admin` role claim grants functional permissions unless `Beacon:Authorization:Roles:HonourHostRoleClaims` is `true`. See [Sign-in & identity](/integrate/authentication/#your-app-owns-sign-in).

**SQL Server metadata.** Replace `Moberg.Beacon.Core.PostgreSql` with `Moberg.Beacon.Core.SqlServer` and `UsePostgreSql()` with `UseSqlServer()`. Warp needs PostgreSQL for its own tables; with SQL Server, use another runner or give Warp a PostgreSQL database.
