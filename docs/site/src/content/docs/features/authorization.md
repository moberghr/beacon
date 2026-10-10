---
title: Authorization
description: Pluggable authorization for Beacon with global roles, project membership and data-source access, database-backed roles, custom providers and host seams.
---

Beacon authorizes in two layers: a caller's **global permission** (read, write) comes from a pluggable `IBeaconAuthorizationProvider`, and its **project access** — which projects, and through them which data sources, it may use — comes from one evaluator, `IProjectAccessEvaluator`. See [Project access](#project-access).

## Overview

The authorization system consists of:

- **IBeaconUserContext** - Provides access to current user information
- **IBeaconAuthorizationProvider** - The caller's global read and write permission
- **IProjectAccessEvaluator** - The caller's project access per action (membership, open projects, API-key project lists, Admins)
- **IBeaconAuthenticationProvider** - Pluggable authentication (the sample uses `DatabaseAuthenticationProvider`)
- **Built-in Providers** - Ready-to-use implementations (Default, Database-Backed)
- **Custom Providers** - Plug in your own authorization logic

### Key Features

- Opt-in by default — authorization is disabled unless explicitly enabled
- Pluggable architecture — integrate with any authentication system
- Cookie-based sessions — a login form is served at the React route `/login`; optional OIDC/SSO and JWT bearer (for MCP) are also supported
- Two layers — global permissions, and project access: project membership, with data sources following their projects
- Backward compatible — existing installations work unchanged
- Framework agnostic — works with ASP.NET Core Identity, OAuth, custom auth, etc.
- Database-backed roles — built-in user management with Admin, Editor, Viewer roles

:::note
For full user management (creating users, assigning roles, login form), see the [User Management Guide](/features/user-management/).
:::

## Quick Start

### 1. Enable Authorization

Update your `Program.cs` to enable authorization:

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.AddBeaconScheduler<BeaconScheduler>();
    options.BaseUrl = "https://localhost:7187";

    // Enable authorization (with Beacon user management, DatabaseAuthorizationProvider is used instead)
    options.Authorization.Enabled = true;
    options.AddAuthorizationProvider<MyAuthorizationProvider>(); // see Custom Authorization Provider
})
.UsePostgreSql(connectionString, "beacon");

// Serves the React SPA (Beacon.UI Razor Class Library) at the root URL "/"
builder.Services.AddBeaconUI();

var app = builder.Build();

// Authentication + authorization middleware
app.UseAuthentication();
app.UseAuthorization();

// Map the React shell + Beacon REST API
app.UseBeaconUI();
```

### 2. Add Role Claims

A host that signs users in itself supplies role claims. Add a claims transformer:

```csharp
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Beacon.Core.Authorization;

public class MyClaimsTransformation : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = (ClaimsIdentity)principal.Identity!;

        // Add Beacon role claim, and the standard role claim the Admin checks read
        identity.AddClaim(new Claim(BeaconClaims.Role, "Admin"));
        identity.AddClaim(new Claim(ClaimTypes.Role, "Admin"));
        identity.AddClaim(new Claim(BeaconClaims.UserId, principal.Identity.Name));
        identity.AddClaim(new Claim(BeaconClaims.UserName, principal.Identity.Name));

        return Task.FromResult(principal);
    }
}

// Register it
builder.Services.AddScoped<IClaimsTransformation, MyClaimsTransformation>();
```

### Admin role claim

Admin-only routes (the `BeaconApiAdmin` policy) and the resource rules that let an Admin act on what another user owns (working someone else's alert task, changing another user's data contract or AI actor, setting a repository access token) read the standard `ClaimTypes.Role` claim with the value `Admin`. A host that signs users in itself must emit that claim for its administrators; `beacon:role` alone is not enough. Beacon's own login and OIDC sign-in emit it from the user's roles. When Beacon user management stores the caller, the user must also be enabled and not archived; an API key never carries a role, so it is never an Admin. For [project access](#project-access), a stored user is an Admin by the roles stored now, not by the claim the session carries, so a demoted Admin loses the Admin bypass on the next request; the claim counts only on a host without Beacon user management, for a caller without a stored user. While user management is on, a session without a stored user reaches no project at all.

### 3. Test Authorization

Start your application and verify:

- Username appears in the React UI (not hardcoded)
- Unauthorized operations return 403 Forbidden
- Different roles have different permissions

## User Context

### Accessing Current User

Inject `IBeaconUserContext` anywhere in your application:

```csharp
public class MyService
{
    private readonly IBeaconUserContext _userContext;

    public MyService(IBeaconUserContext userContext)
    {
        _userContext = userContext;
    }

    public void DoSomething()
    {
        var userId = _userContext.UserId;
        var userName = _userContext.UserName;
        var email = _userContext.Email;
        var isAuthenticated = _userContext.IsAuthenticated;

        if (_userContext.HasClaim(BeaconClaims.Role, "Admin"))
        {
            // Admin-only logic
        }
    }
}
```

### Standard Claims

Use these standard claim types for consistency:

```csharp
BeaconClaims.UserId       // "beacon:user_id"
BeaconClaims.UserName     // "beacon:user_name"
BeaconClaims.Role         // "beacon:role"
BeaconClaims.Permission   // "beacon:permission"
```

## Built-in Authorization Providers

### DefaultAuthorizationProvider

Allows all operations (backward compatible default).

**Use case:** When you don't need authorization or handle it elsewhere.

```csharp
// Authorization disabled (default behavior)
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    // Authorization.Enabled = false by default
    options.AddBeaconScheduler<BeaconScheduler>();
})
.UsePostgreSql(connectionString, "beacon");
```

### DatabaseAuthorizationProvider

Database-backed authorization that reads roles from Beacon's user management tables. This is the recommended provider when using the built-in [User Management](/features/user-management/) system.

| Role | Level | Read | Create/Edit/Execute | Delete/Archive |
|------|-------|------|---------------------|----------------|
| **Admin** | 3 | Yes | Yes | Yes |
| **Editor** | 2 | Yes | Yes | No |
| **Viewer** | 1 | Yes | No | No |

**Use case:** When using Beacon's built-in user management with login form and role assignment.

```csharp
options.Authorization.Enabled = true;

// Enable user management + database auth
options.Authentication.EnableLoginForm = true;
options.AddAuthenticationProvider<DatabaseAuthenticationProvider>();

options.UserManagement = new UserManagementOptions
{
    Enabled = true,
    AllowInternalUsers = true
};
```

No claims transformer needed — roles are loaded directly from the database.

:::note
`DatabaseAuthorizationProvider` is automatically registered when user management is enabled. It also supports `IsSuperAdmin` to bypass all checks.
:::

## Custom Authorization Provider

Implement `IBeaconAuthorizationProvider` to create custom authorization logic:

```csharp
using Beacon.Core.Authorization;

public class MyAuthorizationProvider : IBeaconAuthorizationProvider
{
    private readonly IBeaconUserContext _userContext;
    private readonly IMyPermissionService _permissionService;

    public MyAuthorizationProvider(
        IBeaconUserContext userContext,
        IMyPermissionService permissionService)
    {
        _userContext = userContext;
        _permissionService = permissionService;
    }

    // Global permissions (required)
    public async Task<bool> HasReadPermissionAsync(
        CancellationToken cancellationToken = default)
    {
        return await _permissionService.HasPermissionAsync(
            _userContext.UserId,
            "beacon.read");
    }

    public async Task<bool> HasWritePermissionAsync(
        CancellationToken cancellationToken = default)
    {
        return await _permissionService.HasPermissionAsync(
            _userContext.UserId,
            "beacon.write");
    }
}
```

The resource-level members (`AuthorizeAsync`, `AuthorizeNewResourceAsync`, `GetAccessibleResourceIdsAsync`) are obsolete: they have default bodies, Beacon never calls them, and they are removed in 5.0. Which projects and resources a caller reaches is decided by [project access](#project-access); a provider's read and write permission caps it.

Register your provider:

```csharp
options.Authorization.Enabled = true;
options.AddAuthorizationProvider<MyAuthorizationProvider>();
```

## Integration Examples

### ASP.NET Core Identity

```csharp
// Add Identity
builder.Services.AddIdentity<ApplicationUser, IdentityRole>()
    .AddEntityFrameworkStores<ApplicationDbContext>();

// Add Beacon with authorization
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.Authorization.Enabled = true;
    options.AddAuthorizationProvider<MyAuthorizationProvider>();
})
.UsePostgreSql(connectionString, "beacon");

// Claims transformer
public class IdentityToBeaconClaimsTransformer : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = (ClaimsIdentity)principal.Identity!;

        // Map Identity roles to Beacon roles
        if (principal.IsInRole("Administrator"))
            identity.AddClaim(new Claim(BeaconClaims.Role, "Admin"));
        else if (principal.IsInRole("PowerUser"))
            identity.AddClaim(new Claim(BeaconClaims.Role, "Editor"));
        else
            identity.AddClaim(new Claim(BeaconClaims.Role, "Viewer"));

        // Add user claims
        identity.AddClaim(new Claim(BeaconClaims.UserId,
            principal.FindFirstValue(ClaimTypes.NameIdentifier)));
        identity.AddClaim(new Claim(BeaconClaims.UserName,
            principal.Identity.Name));

        return Task.FromResult(principal);
    }
}

builder.Services.AddScoped<IClaimsTransformation, IdentityToBeaconClaimsTransformer>();
```

### OAuth 2.0 / OpenID Connect

```csharp
// Add authentication
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
.AddCookie()
.AddOpenIdConnect(options =>
{
    options.Authority = "https://your-identity-provider.com";
    options.ClientId = "your-client-id";
    options.ClientSecret = "your-client-secret";
    options.ResponseType = "code";
    options.SaveTokens = true;
});

// Add Beacon with authorization
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.Authorization.Enabled = true;
    options.AddAuthorizationProvider<MyAuthorizationProvider>();
})
.UsePostgreSql(connectionString, "beacon");

// Claims transformer
public class OAuthToBeaconClaimsTransformer : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var identity = (ClaimsIdentity)principal.Identity!;

        // Map OAuth roles to Beacon roles
        var role = principal.FindFirstValue("role") switch
        {
            "admin" => "Admin",
            "user" => "Editor",
            _ => "Viewer"
        };

        identity.AddClaim(new Claim(BeaconClaims.Role, role));
        identity.AddClaim(new Claim(BeaconClaims.UserId,
            principal.FindFirstValue("sub")));
        identity.AddClaim(new Claim(BeaconClaims.UserName,
            principal.FindFirstValue("name")));

        return Task.FromResult(principal);
    }
}
```

### External Authorization Service

```csharp
// Your external auth service
public interface IExternalAuthService
{
    Task<bool> CheckPermissionAsync(string userId, string permission);
    Task<string[]> GetUserRolesAsync(string userId);
}

// Custom authorization provider
public class ExternalAuthProvider : IBeaconAuthorizationProvider
{
    private readonly IBeaconUserContext _userContext;
    private readonly IExternalAuthService _externalAuth;

    public ExternalAuthProvider(
        IBeaconUserContext userContext,
        IExternalAuthService externalAuth)
    {
        _userContext = userContext;
        _externalAuth = externalAuth;
    }

    public async Task<bool> HasReadPermissionAsync(
        CancellationToken cancellationToken = default)
    {
        return await _externalAuth.CheckPermissionAsync(
            _userContext.UserId,
            "beacon.read");
    }

    public async Task<bool> HasWritePermissionAsync(
        CancellationToken cancellationToken = default)
    {
        return await _externalAuth.CheckPermissionAsync(
            _userContext.UserId,
            "beacon.write");
    }

    // Implement other methods...
}

// Register it
builder.Services.AddScoped<IExternalAuthService, YourExternalAuthService>();
options.AddAuthorizationProvider<ExternalAuthProvider>();
```

## Project access

Projects are the authorization boundary. A caller reaches a project's queries, subscriptions, stored runs, tasks, data contracts, AI actors, documentation and data-source metadata only through a project it may use, and a data source only through a project that links it. One evaluator, `IProjectAccessEvaluator`, decides this for every caller once per request; the REST API, the UI and the MCP playground consult it.

### Open and restricted projects

- **Open** (the default, and every project that existed before this version): everyone with a Beacon role uses the project at their global role — a Viewer reads and runs saved SQL, an Editor also writes and works ad hoc.
- **Restricted**: only the project's members and Admins use it; to anyone else it does not exist (`404`). An Admin restricts or opens a project with the **Access** card on the project page (`PUT /beacon/api/projects/{id}/access` with `{ "requiresMembership": true }`). Add members — a project admin first — before restricting. The card lists the project's data sources another open project also links, which stay reachable through that project, and every other project, open or restricted, that links any of its data sources, with that project's admins (see [Unlinking](#unlinking-data-sources)); the `PUT` answer carries the same list as `sharedSourceProjects`. Restricting answers `409` while project membership is unavailable; opening is always possible.
- **`Beacon:Authorization:RequireProjectMembership: true`** restricts every project, whatever its own setting, and makes data sources no project links Admin-only. It needs project memberships (Beacon user management, or a host [membership source](#host-seams)); the host fails at startup otherwise.
- **New projects** are created open in this version. When the creator is an active Beacon user, they become its project admin, so they can add members before an Admin restricts it.
- **Archived projects** grant nothing. No route archives a project today.

### Project roles

A member holds one role in a project. The **effective role** is the lower of the membership role and the caller's global role (a global Viewer is a Viewer in every project); in an open project, the open grant applies as well and the higher of the two wins.

| Action | Viewer | Editor (restricted project) | Editor (open project) | Project admin | Admin |
|---|---|---|---|---|---|
| Read: see resources, SQL, metadata, stored results, documentation | ✓ | ✓ | ✓ | ✓ | ✓ |
| Run: execute saved SQL, evaluate data contracts | ✓ | ✓ | ✓ | ✓ | ✓ |
| Query: ad-hoc and draft SQL, LLM work (playground, AI actors) | – | ✓ | ✓ | ✓ | ✓ |
| Write: create and change resources | – | ✓ | ✓ | ✓ | ✓ |
| Manage: delete, archive, take over a lock, force a metadata refresh | – | only as the recorded owner | ✓ | ✓ | ✓ |
| Manage members and link data sources | – | – | – | ✓ | ✓ |
| Restrict or open the project | – | – | – | – | ✓ |

The recorded owner is a data contract's or subscription's owner, an AI actor's creator, and a query's lock holder. The ownership rules on tasks, contracts and AI actors still apply on top: both the project action and the owner rule must pass.

### Data sources follow their projects

- A data source is reachable only through a project the caller may use. A resource that reads several data sources is reachable only through **one** project that links all of them. Resources addressed by their data sources alone (an ad-hoc preview, a metadata read, a data contract) have no home project: any such project serves.
- **Unlinked data sources.** A data source no project links stays reachable as before only if it existed before this version (`OpenWhenUnlinked`, true for those rows). A new data source is Admin-only until it is linked — link it on create (the **Projects** field of the create dialog, `projectIds` on `POST /beacon/api/data-sources`).
- **Shared data sources.** A source linked to an open project is reachable through that project by everyone with a role, even when a restricted project links it too. Writes to a source's shared state — relationships, metadata description edits, a forced metadata refresh, documentation patches that write descriptions — need Write in **every** live project that links it, or an Admin, because that state grounds every project that uses the source.
- **Linking.** A project admin (or an Admin) links data sources into the project from its page (**Link data sources**, `POST /beacon/api/projects/{id}/data-sources`). Anyone but an Admin may link only sources already linked to a live open project; a host's data source links only into the project the host declares for it (the project with the same host key), for Admins too. An unreadable or missing source answers `404`, a readable one that may not be linked `403`, and nothing is linked partially. The same rules apply to the data sources named when a project is created (`dataSourceIds` and `repositoryUrls` may be omitted or `null`: none). Each link records who created it; links the host creates, and links that existed before this version, record no one.
- **Queries without steps** read no data source, so no project holds them: their creator (recorded as the query's owner) reads and changes them, and Admins; once the query has steps, the project rule above applies to it and to the work derived from it. A step-less query created before owners were recorded has no owner: it is reachable while every project is open and every data source openly reachable (and the caller carries no project list), and Admin-only otherwise.
- **One database login per boundary.** Project access decides who may run SQL through Beacon, not what the database login may read. Give each data source a login that sees only what the projects linking it should see.

### Who the caller is

| Caller | Project access |
|---|---|
| Admin (enabled user with the Admin role, or a super admin) | every live project, every action |
| Signed-in Beacon user | open projects at their global role; restricted projects through membership |
| Disabled or archived user | nothing |
| Session with no stored user while user management is on (a host principal, a bearer token not bound to a user) | nothing: with user management on, every caller that may use Beacon has a stored user |
| Session on a host without Beacon user management | open projects at the global role; the `Admin` role claim counts; a restricted project is Admin-only unless the host supplies a [membership source](#host-seams) |
| API key with a project list | its owner's access, within the list, capped by the lower of the key's scope and the owner's global role (`Read` keys read and run only; an owner who may neither read nor write gives the key nothing); never the Admin bypass |
| API key without a project list (created before lists were required) | its owner's access over REST, capped the same way |
| Work with no HTTP request (background jobs, startup) | the system, unrestricted, keeping per-project data scoping (see [system work](#host-seams)) |

Keys and MCP callers never manage members. See [API Keys](/features/api-keys/#project-restrictions) and the [MCP Server](/features/mcp-server/#mcp-playground) for their rules.

The global role that caps a signed-in caller comes from the authorization provider: write permission makes it an Editor, read permission a Viewer. With Beacon user management on, the stored roles cap it even when `Beacon:Authorization:Enabled` is `false`; only with both off is every signed-in caller an Editor.

### How it is enforced

- **Every request declares its check.** A request that addresses a resource by id implements `IProjectAuthorizedRequest` and names the resource and the action; a MediatR pipeline behaviour resolves the resource's project (or data sources) and refuses before the handler runs — `404` when the resource is missing or unreadable, `403` when it is readable but the action is not allowed. A list request implements `IProjectFilteredRequest` and filters in SQL, so totals and pages never count what the caller cannot see. A request with nothing project-scoped is marked `[ProjectScopeExempt("reason")]`. A test fails when a request property ending in `Id` or `Ids` is neither checked nor exempt.
- **Requests sent in-process are checked too.** The behaviour runs on every `IMediator.Send` made while an HTTP request is served, so a host endpoint that sends Beacon requests is checked against that request's caller (`404`/`403`), not trusted.
- **Problem details.** Refusals throw `BeaconForbiddenException` (`403`) and `BeaconNotFoundException` (`404`). On `/beacon/api` these become RFC 7807 responses without their message (a caller cannot tell a resource it may not see from a missing one), and `BeaconConflictException` and `BeaconRateLimitedException` become `409` and `429` with theirs — in every host, without the sample's middleware. Only these Beacon exceptions are mapped; any other exception, including `UnauthorizedAccessException`, is left to the host's exception handling. Each mapping is logged with the exception type, the route name and the route's ids.
- **Security log events.** Refusals are logged as `AccessRefused` (7108, a `403`) and `AccessHidden` (7109, a `404` for a resource that is missing or unreadable), with the resource and its id, the reason (`not-readable`, `missing` or `not-granted`), the principal kind, the API key's id and the caller's Beacon user id (its external id when it has no stored user) — identifiers only; a missing resource logs at Information, the rest at Warning. Membership, access-mode, link and repository changes have their own events (7101–7107, 7110; see [Upgrading](#upgrading-from-45-project-access)).
- **The UI** hides what a caller may not do: the **Members** tab (shown to project admins and Admins while Beacon manages membership), **Link data sources**, and the **Access** card (Admins), which says whether members are managed by the host or membership is unavailable. A resource the caller may not read shows the not-found state.
- **The MCP playground** checks the caller's project access for every tool call (Read for the catalog tools, Query for `ask`, `query`, `dry_run` and `feedback`) and refuses a call without a signed-in request.
- **Real-time events** on the SignalR hub may still carry resource ids to a member whose access was just revoked; the resources themselves are refused.

### Members

A project admin or an Admin manages members on the project page's **Members** tab: search people by display name (case-insensitive, at least two characters; up to 20 matches; rate-limited), add them with a role, change a role, remove a member. People are shown by display name, or as "User {id}" without one — never by user name, e-mail address or external id — and people without a display name are not found by the search. A disabled or archived user stays listed as inactive and grants nothing.

A project keeps an **effective project admin**: a member with the project admin role who is enabled, not archived, and holds a global role of Editor or above (a project admin capped at Viewer manages nothing). Removing or demoting the last one is refused (`400`) unless an Admin does it. The check and the change run in one serializable transaction, so two concurrent changes cannot both pass; a change that collides with another answers `409` (reload and retry). The routes:

| Route | Purpose |
|---|---|
| `GET /beacon/api/projects/{id}/members` | members, paged |
| `GET /beacon/api/projects/{id}/member-candidates?search=` | people to add |
| `PUT /beacon/api/projects/{id}/members/{userId}` | add a member or change their role (`{ "role": 1 \| 2 \| 3 }`: Viewer, Editor, ProjectAdmin) |
| `DELETE /beacon/api/projects/{id}/members/{userId}` | remove a member |
| `PUT /beacon/api/projects/{id}/access` | restrict or open (Admin) |
| `POST /beacon/api/projects/{id}/data-sources` | link data sources (`{ "dataSourceIds": [...] }`) |
| `DELETE /beacon/api/projects/{id}/data-sources/{dataSourceId}` | unlink a data source (Admin) |

When the host manages membership, the membership routes answer `409` ("Project membership is managed by the host."); when membership is unavailable, they answer `409` ("Project membership is not available in this deployment."). The Members tab is hidden in both cases. Restricting a project answers `409` only while membership is unavailable: with a host membership source, an Admin may restrict a project and the host's memberships decide who reaches it. Linking data sources needs the project-admin role however membership is managed.

Project lists carry `requiresMembership`, `myRole` and `memberCount`; `GET /beacon/api/projects?forApiKey=true` lists only the projects a new API key of the caller may name (those it reads without the Admin bypass). Project details also carry `canManageMembers`, `openlyReachableDataSourceIds`, `membershipManagement` (`1` Beacon, `2` the host, `3` unavailable) and, for an Admin, `sharedSourceProjects`.

### Unlinking data sources

An Admin removes a data source's link to a project with `DELETE /beacon/api/projects/{id}/data-sources/{dataSourceId}`, or from the **Access** card of a project that shares the source: the card lists every other live project, open or restricted, that links any of the project's data sources, with the shared sources and that project's active admins, and unlinks a shared source from one of them after confirming.

- Members of the project lose the data source through it at once; other projects that link it keep it.
- When the removed link was the data source's last, the source becomes Admin-only until it is linked again: a data source never becomes unlinked and open.
- A host's data source cannot be unlinked (`409`; the host links it). A link that does not exist answers `404`, and a concurrent change to the source's links `409` (retry).
- Each unlink is logged as `DataSourceUnlinked` (7110), with whether it left the source unlinked.

### Host seams

All defaults are registered with `TryAdd`: register your own implementation before `AddBeaconServices`, or replace the registration.

- **`IProjectMembershipSource`** — where memberships come from, for example IdP groups. `IsAvailable` is read once at startup (the default reports whether Beacon user management is on); `GetMembershipsAsync` returns the caller's role per project id. A `null` result or an exception denies every restricted project for that request, never opens one, and a membership whose role is not a defined `ProjectRole` value is dropped (a warning logs how many). With a custom source, Beacon's membership routes answer `409` and the UI hides the Members tab.
- **`IRunAsCeilingSource`** — the global role that caps an actor who is not the request's signed-in caller (the default reads the user's stored roles).
- **`ISystemWorkScope`** — marks a DI scope as work Beacon does for itself (a background job, a startup task). A host that runs Beacon's jobs on its own job host calls `MarkAsSystemWork()` on each job's scope before the job runs; the sample's Warp host and Beacon's own startup scopes do this. Work without an HTTP request in an unmarked scope is still treated as the system, and logs a warning (at most once a minute).
- **`IProjectAccessEvaluator`** — the whole decision, returning a `ProjectAccessScope` (`IsUnrestricted`, `For(action)`, `RoleIn(projectId)`, `Allows(projectId, action)`).

`IBeaconAuthorizationProvider` stays the global ceiling: `HasReadPermissionAsync` and `HasWritePermissionAsync` are still called. Its resource-level members and `AuthorizationOptions.EnableResourceLevelAuthorization` are `[Obsolete]`, never called, and removed in 5.0; `ResourceType` and `PermissionAction` remain only for them.

## Advanced Scenarios

### The caller as a resource owner

Alert tasks (assignee, resolver), data contracts (owner) and AI actors (creator) store the caller's `Users.ExternalId`. Handlers get the caller from `IBeaconActorAccessor` (scoped, registered with `TryAddScoped`, so a host can replace it), which returns a `BeaconActor`: its `UserId` and whether it `IsAdmin` (see [Admin role claim](#admin-role-claim)). An API key is its owner, found by the key's owner id; any other session is the user its `NameIdentifier` names. A session with no stored user (a host without Beacon user management) keeps the `NameIdentifier` it presents; a disabled or archived user, an API key whose owner is not found, and an anonymous request have no `UserId` and own nothing. These rules sit beside the authorization provider: a custom `IBeaconAuthorizationProvider` decides read and write permission, not who owns a task, contract or actor.

### Permission Caching

Cache permissions for better performance:

```csharp
public class CachedAuthorizationProvider : IBeaconAuthorizationProvider
{
    private readonly IBeaconUserContext _userContext;
    private readonly IMemoryCache _cache;
    private readonly IActualAuthProvider _actualProvider;

    public async Task<bool> HasWritePermissionAsync(
        CancellationToken cancellationToken = default)
    {
        var cacheKey = $"write_perm_{_userContext.UserId}";

        return await _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
            return await _actualProvider.HasWritePermissionAsync(cancellationToken);
        });
    }
}
```

## Audit Trail (Future)

Optional audit fields are available on entities for future audit trail support:

```csharp
public abstract class AuditableBaseEntity : BaseEntity
{
    public string? CreatedByUserId { get; set; }
    public string? CreatedByUserName { get; set; }
    public DateTime? ModifiedTime { get; set; }
    public string? ModifiedByUserId { get; set; }
    public string? ModifiedByUserName { get; set; }
}
```

**Note:** These fields are currently nullable and not automatically populated. A future release will include an EF Core interceptor to populate these fields automatically when authorization is enabled.

## Configuration Options

### Authorization Options

```csharp
public class AuthorizationOptions
{
    /// <summary>
    /// Enable authorization checks. Default: false
    /// </summary>
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Authorization provider type. If null, uses DefaultAuthorizationProvider.
    /// </summary>
    public Type? ProviderType { get; set; }

    /// <summary>
    /// Never read; removed in 5.0. Project and resource access is decided by IProjectAccessEvaluator.
    /// </summary>
    [Obsolete]
    public bool EnableResourceLevelAuthorization { get; set; } = false;
}
```

Project access has its own options, bound from `Beacon:Authorization`:

```json
{
  "Beacon": {
    "Authorization": {
      "RequireProjectMembership": false
    }
  }
}
```

`RequireProjectMembership: true` restricts every project and makes data sources no project links Admin-only (see [Project access](#open-and-restricted-projects)). It is validated at startup: without Beacon user management and without a host `IProjectMembershipSource`, the host does not start.

### Example Configuration

```csharp
builder.Services.AddBeaconServices(builder.Configuration, options =>
{
    options.AddBeaconScheduler<BeaconScheduler>();
    options.BaseUrl = "https://localhost:7187";

    // Authorization configuration
    options.Authorization.Enabled = true;
    options.AddAuthorizationProvider<MyAuthorizationProvider>();
})
.UsePostgreSql(connectionString, "beacon");
```

## Troubleshooting

### Authorization Not Working

**Problem:** Users can access resources they shouldn't.

**Solution:**

1. Verify authorization is enabled:
   ```csharp
   options.Authorization.Enabled = true;
   ```

2. Verify `UseAuthorization()` is called:
   ```csharp
   app.UseAuthentication();
   app.UseAuthorization(); // ← This must be present
   app.UseBeaconUI();      // serves the React SPA at "/"
   ```

3. Check claims are added correctly:
   ```csharp
   // Add logging to your claims transformer
   _logger.LogInformation("User {User} assigned role {Role}",
       principal.Identity.Name, role);
   ```

### Username Not Displayed

**Problem:** The React UI shows "Guest" or nothing.

**Solution:**

1. Verify `IBeaconUserContext` is registered (automatic with `AddBeaconUI()`)
2. Check that the claims transformer adds `BeaconClaims.UserName`
3. Verify the user is authenticated

### 403 Forbidden on All Requests

**Problem:** All requests return 403 Forbidden.

**Solution:**

1. Check the authorization provider is returning `true` for authenticated users
2. Add logging to the authorization provider:
   ```csharp
   _logger.LogWarning("Authorization denied for {User}: {Reason}",
       _userContext.UserName, result.FailureReason);
   ```
3. Verify the claims transformer is executed

### Provider Not Called

**Problem:** Authorization provider methods never execute.

**Solution:**

1. Verify the provider is registered:
   ```csharp
   options.AddAuthorizationProvider<MyAuthorizationProvider>();
   ```
2. Check authorization is enabled
3. Ensure `UseAuthorization()` middleware is added

## Security Best Practices

1. **Always validate on the server** - Client-side checks are for UX only
2. **Use HTTPS in production** - Protect credentials and session cookies
3. **Store role assignments in the database** - Don't hardcode in the claims transformer
4. **Implement rate limiting** - Prevent brute force attacks
5. **Audit authorization failures** - Log all 403 responses with context
6. **Use strong session management** - Implement proper timeout and renewal
7. **Validate resource ownership** - Don't rely on resource IDs alone
8. **Restrict sensitive projects** - Add members (a project admin first), then restrict the project, and give each data source a database login that sees only what its projects should see
9. **Principle of least privilege** - Default to the most restrictive permissions

## Upgrading from 4.5: project access

This version makes projects the authorization boundary on the REST API, the UI and the MCP playground (see [Project access](#project-access)). In this version, MCP tool calls over `/beacon/mcp`, scheduled runs and AI background work keep their previous project rules: the project lists of API keys and MCP callers, and scheduled work runs as the system.

**Schema.** The `AddProjectAuthorization` migration (PostgreSQL `20261011120000`, SQL Server `20261011120001`) adds the `project_members` table and seven columns: `projects.requires_membership` (default `false`, so every existing project stays open), `data_sources.open_when_unlinked` (default `true` for existing rows; new data sources are stored with `false`), `subscriptions.owner_user_id`, `queries.owner_user_id`, `query_versions.data_source_ids`, `project_data_sources.linked_by_user_id` and `query_step_change_history.data_source_id` (all null for existing rows). Beacon does not migrate its schema at startup: apply the migration as you apply the others. Rolling it back drops the table and the seven columns, and with them memberships, subscription and query owners, who linked each data source, and the data sources recorded on query versions and step history.

**What changes without any configuration:**

- Existing projects and existing data sources stay open, so interactive users keep reaching what they reach today — except where the next points say otherwise.
- **Stored runs made before runs recorded their data sources** are shown by their subscription's current query: to interactive users and to API keys without a project list while that query is readable, also with `RequireProjectMembership` on. Callers confined to a project list do not see them, and a run whose subscription or query is archived or missing, or whose query reads no data source, is Admin-only.
- **Queries without steps** are reachable only by their creator and Admins, once any project is restricted or archived or any data source is not openly reachable. Queries created before this version have no recorded owner, so such a query without steps is then Admin-only; add a step (as an Admin) to give it a project.
- **API keys** with a project list are confined to those projects on every REST route, not only on MCP. A key never exceeds its owner: it is capped by the lower of its scope and its owner's global role, and a key whose owner may neither read nor write reaches nothing. New keys must name at least one project (`400` otherwise; `404` for a project the creator cannot read without the Admin bypass). A key created before lists were required acts with its owner's access over REST.
- **Sessions without a stored user** reach nothing while Beacon user management is on (a host principal, a bearer token not bound to a user); the `Admin` role claim counts for them only on hosts without user management. `Beacon:Authorization:Enabled: false` with user management on still caps every signed-in user by their stored roles.
- **New data sources** are Admin-only until a project links them; link them on create.
- **Linking**: anyone but an Admin links only data sources already in a live open project; a host's data source links only into the project the host declares for it (same host key), for Admins too. An Admin can now unlink a data source (`DELETE /beacon/api/projects/{id}/data-sources/{dataSourceId}`); removing a source's last link makes it Admin-only.
- **Host data sources moved by the host sync**: when the project a host declares for a data source changes, the source moves to it. If a live restricted project held the source, the host project becomes restricted and takes that project's members; when that is not possible — the host manages membership, several live restricted projects held it, or the host project has members the restricted one does not — the source stays where it is and an error is logged.
- Writes to a shared data source's relationships, descriptions and metadata refresh need Write in every project that links it, or an Admin. Relationship routes act only on the route's data source: a relationship id of another data source answers `404`.
- A resource the caller may not read answers `404` like a missing one, and a refused action `403`; Beacon maps these itself instead of answering `500` in hosts without the sample middleware. Refusals now throw `BeaconForbiddenException`, not `UnauthorizedAccessException`, and the API maps only Beacon's exceptions: a host that mapped `UnauthorizedAccessException` keeps doing so for its own code.
- **Beacon requests a host sends in-process** with `IMediator.Send` during an HTTP request are authorized against that request's caller and may now answer `404` or `403`.
- **Project membership** routes: a project keeps an effective project admin (enabled, not archived, global role Editor or above); concurrent membership changes answer `409`. Member candidates match display names only, and people without a display name are shown as "User {id}".
- **Query versions** are read by the data sources each version recorded, in the version list as on a version's own page: after a query moves to other data sources, a caller who cannot read the sources an earlier version recorded does not see that version, and the query's MCP tool card does not name an approved version the caller cannot read.
- **Feedback verdicts** on a query signal (the `feedback` tool and `POST /beacon/api/eval/feedback`) are recorded only by the signal's own caller, a project admin of its project, or an Admin (`403` otherwise).
- **Counts**: a data source's query count includes only the queries the caller may read; migration job counts on data sources and the Home migration summary are an Admin's (zero for anyone else); learning statistics leave out signals without a project for anyone but an Admin.
- Subscriptions record their owner (the signed-in creator; an AI actor's creator for the subscriptions it creates), shown on the subscription page by display name. While Beacon user management is on, a caller without a Beacon user cannot create subscriptions, data contracts or AI actors (`403`).
- **Background work**: a host that runs Beacon's jobs on its own job host marks each job's DI scope as system work (`ISystemWorkScope.MarkAsSystemWork()`). Work without a request in an unmarked scope still runs as the system, and logs a warning (at most once a minute).
- **Host membership sources**: memberships whose role is not a defined `ProjectRole` value are dropped (a warning logs how many).
- `PUT /beacon/api/projects/{id}/access` answers `409` only when restricting while project membership is unavailable; with a host membership source, an Admin may restrict a project.
- Archived projects grant nothing (no route archives projects today).
- New projects are still created open; a creator who is an active Beacon user becomes the project's admin.

**Admin claim.** For project access, a caller stored by Beacon user management is an Admin by its current roles; the session's `Admin` claim counts only for a caller without a stored user on a host without user management (see [Admin role claim](#admin-role-claim)). The `BeaconApiAdmin` policy on Admin-only routes still reads the session's claims.

**Public API.**

- New in `Beacon.Core.Authorization`: `IProjectAccessEvaluator`, `ProjectAccessScope`, `ProjectSet`, `ProjectRole`, `ProjectAction`, `ProjectMembershipManagement`, `IProjectMembershipSource`, `IRunAsCeilingSource`, `ISystemWorkScope`, `IProjectAuthorizedRequest`, `IProjectFilteredRequest`, `ProjectCheck`, `ProjectResource`, `ProjectResourceKind`, `ProjectScopeExemptAttribute`, `ScheduledWorkOwner`, `AccessRefusal`; `BeaconActor.LogId`. In `Beacon.Core.Authorization.ProjectAccess`, `ProjectAccessQueryableExtensions` and `ProjectAccessEvents`: security log event ids 7101 `MemberAdded`, 7102 `MemberRoleChanged`, 7103 `MemberRemoved`, 7104 `AccessModeChanged`, 7105 `DataSourcesLinked`, 7106 `RepositoryScanRequested`, 7107 `RepositoryTokenChanged`, 7108 `AccessRefused`, 7109 `AccessHidden`, 7110 `DataSourceUnlinked` (identifiers only). In `Beacon.Core.Configuration`, `ProjectAuthorizationOptions`; in `Beacon.Core.Models`, `BeaconForbiddenException`, `BeaconNotFoundException`, `BeaconConflictException` and `BeaconRateLimitedException`. All defaults are registered with `TryAdd`.
- `IBeaconActorAccessor.ResolveOwnerAsync` is a new member; hosts are not expected to implement the accessor. `StoredRunScope` gains `From(ProjectSet)` and `IncludesOpen`.
- `IBeaconAuthorizationProvider.AuthorizeAsync`, `AuthorizeNewResourceAsync` and `GetAccessibleResourceIdsAsync`, and `AuthorizationOptions.EnableResourceLevelAuthorization`, are `[Obsolete]` with default bodies and never called; they are removed in 5.0. A custom provider recompiles unchanged; code that calls these members gets `CS0618` warnings, which fail the build where warnings are treated as errors.
- Signature changes on `Beacon.AI`'s `IKnowledgeGraphService`: `GetTableKnowledgeAsync` and `GetLineageAsync` take the caller's `projectId`, and `GetDataSourceKnowledgeAsync(dataSourceId, projectId, ct)` does too: documentation and code references come from that project only.
- `IQueryApprovalService.GetPendingApprovalsAsync(request, queryId, readable, ct)` takes the `ProjectSet` of the projects the caller reads, and counts only the approvals in them.
- `IQueryVersionService.GetVersionsAsync(queryId, request, readable, ct)` takes the `ProjectSet` of the projects the caller reads, and lists and counts only the versions it reads by the data sources each one recorded.
- `VerifySchemaRelationshipCommand(DataSourceId, Id, IsVerified)`, `DeleteSchemaRelationshipCommand(DataSourceId, Id)` and `UpdateSchemaRelationshipCommand.DataSourceId` name the relationship's data source. `GetQueriesRequest`, `GetTasksRequest` and `GetControlTowerDataRequest` require `Readable` (the projects to list from).
- Positional records changed shape, so code that constructs or deconstructs them recompiles (binary-incompatible): `ProjectDataSourceEntry` (a leading `Id`), `ProjectSummaryEntry` (+ `RequiresMembership`, `MyRole`, `MemberCount`), `ProjectDetailEntry` (+ `RequiresMembership`, `MyRole`, `CanManageMembers`, `OpenlyReachableDataSourceIds`, `SharedSourceProjects`, `MembershipManagement`), `CreateDataSourceCommand` (+ an optional `ProjectIds`) and `SubscriptionDetail` (+ `HasOwner`, `OwnerDisplayName`; the owner's id is not returned). `CreateProjectCommand.DataSourceIds` and `RepositoryUrls` are nullable (`null` means none).
- Additive: data-source entries carry `openWhenUnlinked`, `projects` and `otherProjectCount`; `GetProjectsQuery.ForApiKey`; `SetProjectAccessModeResult.SharedSourceProjects`; `SharedSourceProjectEntry`; `UnlinkProjectDataSourceCommand` and its result; new membership and link commands and routes (see [Members](#members)).

**Recommended steps for a regulated deployment:** upgrade; give each data source a database login per boundary; add members (a project admin first) and restrict sensitive projects, checking the list of data sources that stay reachable through open projects; re-issue integration keys with explicit project lists; check the other projects each sensitive project shares data sources with (the **Access** card), and unlink what should not be shared; finally set `Beacon:Authorization:RequireProjectMembership: true`, which restricts every project and makes unlinked data sources Admin-only.

## Migration from Previous Versions

:::caution[Upgrading]
The Admin checks on routes and resources read the `ClaimTypes.Role` claim `Admin` (see [Admin role claim](#admin-role-claim)): a host that only emits `beacon:role` must add it. New public types: `BeaconActor` and `IBeaconActorAccessor` (registered by Beacon; replaceable). `IActorUserResolver` now resolves an API key to its owner by the key's owner id.
:::

If you are upgrading from a version without authorization:

1. **No changes required** - Authorization is disabled by default
2. **Opt-in when ready** - Enable authorization when you're ready
3. **No breaking changes** - Existing code continues to work
4. **Gradual adoption** - Start with global permissions, add resource-level later

## See Also

- [User Management](/features/user-management/) - Built-in user management with login form and role assignment
- [Admin Settings](/features/admin-settings/) - Runtime configuration (Admin-only)
- [Configuration Guide](/getting-started/configuration/)
- [Quick Start](/getting-started/quick-start/)
